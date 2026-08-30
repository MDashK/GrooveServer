using System.Text;

namespace GrooveServer.Pak;

/// <summary>
/// As constantes e as transformacoes pequenas do formato XIP2 — tudo o que nao e' nem a
/// cifra (Pak/XipKeys.cs) nem a compressao (Pak/Lzo1x.cs).
/// </summary>
public static class XipFormat
{
    /// <summary>Tamanho do descritor de cada ficheiro, a' frente dos dados.</summary>
    public const int TamanhoDescritor = 284;      // 0x11C

    /// <summary>Espaco do caminho dentro do descritor, em +12.</summary>
    public const int TamanhoNome = 260;           // MAX_PATH


    /// <summary>O bloco secreto tem 24 bytes cifrados que dao 12 em claro.</summary>
    public const int TamanhoSecreto = 24;

    /// <summary>Indice de chave com que o bloco secreto e' cifrado.</summary>
    public const int ChaveDoSecreto = 12;

    /// <summary>
    /// Quantos bytes do inicio do fluxo comprimido vao cifrados. Nos 5162 blocos do
    /// system.pak a regra e' sempre esta: 40 bytes, ou o que houver se for menos, arredondado
    /// para baixo a um multiplo de 4 (a cifra trabalha em palavras de 32 bits).
    /// </summary>
    public const int MaximoCifrado = 40;

    public static int TamanhoRsa(int comprimido) => Math.Min(MaximoCifrado, comprimido) / 4 * 4;

    /// <summary>
    /// Extensoes cujo conteudo esta' mascarado por cima da compressao. Sao os ficheiros de
    /// configuracao em texto; repare-se no <c>.cvs</c> — nao e' gralha desta lista, e' mesmo
    /// assim que esta' no cliente, e por isso um <c>.csv</c> NAO leva mascara.
    /// </summary>
    public static readonly HashSet<string> Mascarados =
        new(StringComparer.OrdinalIgnoreCase) { ".gsi", ".cvs", ".gds", ".txt", ".ini", ".vgi", ".crc" };

    /// <summary>Extensoes com uma segunda camada de XOR por cima de tudo (video).</summary>
    public static readonly HashSet<string> VisualClip =
        new(StringComparer.OrdinalIgnoreCase) { ".vc", ".vce", ".vci" };

    public static bool EMascarado(string nome) =>
        nome.Length >= 4 && Mascarados.Contains(nome[^4..]);

    // ------------------------------------------------------------------ XOR do descritor

    /// <summary>
    /// A chave com que o descritor de cada ficheiro esta' cifrado: 256 bytes tirados de um
    /// texto japones em shift-jis. Nao e' esconderijo nenhum — e' um XOR — mas sem ela nem o
    /// nome do ficheiro se le'.
    /// </summary>
    private static readonly byte[] ChaveJap = ConstruirChaveJap();

    private static byte[] ConstruirChaveJap()
    {
        const string texto =
            "……耕一さん……あなたを殺します\n" +
            "私はあなたを、愛してはいませんから…\n" +
            "生きて…ラカン…\n" +
            "百年…貴方を待っていたの…千年…貴方に恋していたわ\n" +
            "私…世界より貴方がほしい……\n" +
            "夜空に星が輝くように溶けた心は離れない\n" +
            "たとえこの手が離れてもふたりがそれを忘れぬ限り";

        Encoding sjis;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            sjis = Encoding.GetEncoding(932);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("without shift-jis there is no key for the descriptor", e);
        }

        var bytes = sjis.GetBytes(texto.Replace("\n", "\r\n"));
        if (bytes.Length < 257) throw new InvalidOperationException("the key text came out short");
        return bytes[1..257];    // o cliente salta o primeiro byte
    }

    /// <summary>
    /// XOR do descritor com a chave, comecando em <paramref name="deslocamento"/>. E' o seu
    /// proprio inverso: a mesma chamada cifra e decifra.
    /// </summary>
    public static byte[] XorDescritor(ReadOnlySpan<byte> dados, int deslocamento)
    {
        var saida = new byte[dados.Length];
        for (int i = 0; i < dados.Length; i++)
            saida[i] = (byte)(ChaveJap[(deslocamento + i) & 255] ^ dados[i]);
        return saida;
    }

    // ------------------------------------------------------------------ mascara dos textos

    private static readonly uint[] ChaveTexto = ConstruirChaveTexto();

    /// <summary>
    /// A tabela da mascara dos textos. No cliente esta' pronta em 0x55B2B0; aqui reconstroi-se
    /// para nao ser preciso arrastar mais um bloco de dados do jogo: e' a tabela do CRC32
    /// baralhada por pedacos.
    /// </summary>
    private static uint[] ConstruirChaveTexto()
    {
        var crc = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c >> 1) ^ ((c & 1) != 0 ? 0xEDB88320u : 0u);
            crc[i] = c;
        }
        var bruto = new byte[1024];
        for (int i = 0; i < 256; i++) BitConverter.TryWriteBytes(bruto.AsSpan(i * 4), crc[i]);

        (int, int)[] pedacos =
        {
            (112, 160), (48, 112), (160, 256), (0, 48), (384, 512), (256, 384),
            (624, 688), (528, 624), (688, 768), (512, 528), (880, 1024), (768, 880),
        };
        var juntos = new byte[1024];
        int op = 0;
        foreach (var (a, b) in pedacos)
        {
            Array.Copy(bruto, a, juntos, op, b - a);
            op += b - a;
        }
        var chave = new uint[256];
        for (int i = 0; i < 256; i++) chave[i] = BitConverter.ToUInt32(juntos, i * 4);
        return chave;
    }

    /// <summary>Tira a mascara de um ficheiro de configuracao.</summary>
    public static byte[] DesmascararTexto(ReadOnlySpan<byte> dados) => Mascara(dados, -1);

    /// <summary>Poe a mascara. O inverso de <see cref="DesmascararTexto"/>.</summary>
    public static byte[] MascararTexto(ReadOnlySpan<byte> dados) => Mascara(dados, +1);

    private static byte[] Mascara(ReadOnlySpan<byte> dados, int sinal)
    {
        var saida = new byte[dados.Length];
        int inicio = dados.Length % 256;
        int palavras = dados.Length / 4;
        for (int i = 0; i < palavras; i++)
        {
            uint v = BitConverter.ToUInt32(dados.Slice(i * 4, 4));
            uint k = ChaveTexto[(inicio + i) % 256];
            BitConverter.TryWriteBytes(saida.AsSpan(i * 4), sinal < 0 ? v - k : v + k);
        }
        dados[(palavras * 4)..].CopyTo(saida.AsSpan(palavras * 4));   // a sobra fica igual
        return saida;
    }

    // ------------------------------------------------------------------ somas

    private static readonly uint[] TabelaCrc = ConstruirTabelaCrc();

    private static uint[] ConstruirTabelaCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c >> 1) ^ ((c & 1) != 0 ? 0xEDB88320u : 0u);
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC32 vulgar (o mesmo do zlib). Vai no descritor em +272.</summary>
    public static uint Crc32(ReadOnlySpan<byte> dados)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in dados) c = (c >> 8) ^ TabelaCrc[(c ^ b) & 0xFF];
        return ~c;
    }

    /// <summary>Soma simples dos bytes. Vai no descritor em +276.</summary>
    public static uint Soma(ReadOnlySpan<byte> dados)
    {
        uint s = 0;
        foreach (var b in dados) s += b;
        return s;
    }

    /// <summary>
    /// A dispersao do caminho, que vai no descritor em +8 e serve ao cliente para encontrar o
    /// ficheiro sem comparar strings. E' a dispersao das strings do Python 1.x — multiplicar
    /// por 1000003 e fazer XOR com o byte — com as maiusculas dobradas para minusculas.
    /// Confirmada nos 5162 blocos do system.pak.
    /// </summary>
    public static uint DispersaoNome(ReadOnlySpan<byte> nome)
    {
        if (nome.Length == 0) return 0xFFFFFFFF;
        byte p = nome[0];
        if (p > 0x40 && p < 0x5B) p += 0x20;
        uint v = (uint)p << 7;
        foreach (var b in nome)
        {
            byte c = b;
            if (c > 0x40 && c < 0x5B) c += 0x20;
            v = v * 0xF4243 ^ c;
        }
        v ^= (uint)nome.Length;
        return v == 0xFFFFFFFF ? 0xFFFFFFFE : v;
    }

    /// <summary>
    /// O numero que o <c>system.crc</c> guarda por cada .pak. NAO e' o CRC do ficheiro todo:
    /// o cliente le' cinco ou seis amostras de 32 bytes espacadas de <c>(tamanho-32)/5</c>,
    /// soma o CRC32 de cada uma e junta-lhes o tamanho. E' o que lhe permite validar um .pak
    /// de 224 MB num instante. Confirmado nas cinco entradas do jogo: recalculadas com esta
    /// funcao, dao exatamente o que esta' no system.crc que veio com o jogo.
    /// </summary>
    public static uint ChecksumDoPak(string caminho)
    {
        long tamanho = new FileInfo(caminho).Length;
        if (tamanho < 32) return ~(uint)tamanho;

        long passo = (tamanho - 32) / 5;
        uint acc = (uint)tamanho;
        using var f = File.OpenRead(caminho);
        var buf = new byte[32];
        for (long off = 0; ; off += passo)
        {
            f.Seek(off, SeekOrigin.Begin);
            f.ReadExactly(buf, 0, 32);
            acc += Crc32(buf);
            if (passo <= 0 || off + passo >= tamanho - 32) break;
        }
        return acc;
    }
}
