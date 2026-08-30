using System.Text;

namespace GrooveServer.Pak;

/// <summary>Um ficheiro dentro do .pak.</summary>
public sealed class XipEntry
{
    /// <summary>Caminho interno, com barras invertidas: <c>System\shop\ItemStock.csv</c>.</summary>
    public required string Nome { get; init; }

    /// <summary>Onde comeca o descritor deste bloco, dentro do .pak.</summary>
    public required int Offset { get; init; }

    /// <summary>Tamanho do bloco SEM o descritor — e' o que o cliente soma para saltar para o seguinte.</summary>
    public required int TamanhoBloco { get; init; }

    /// <summary>Tamanho do ficheiro depois de descomprimido.</summary>
    public required int TamanhoFinal { get; init; }

    /// <summary>Dispersao do <see cref="Nome"/>; ver <see cref="XipFormat.DispersaoNome"/>.</summary>
    public required uint Dispersao { get; init; }

    public required uint Crc32 { get; init; }
    public required uint SomaBytes { get; init; }

    /// <summary>
    /// Os quatro bytes em +280: o OFFSET ABSOLUTO dos dados deste bloco no .pak, ou seja
    /// <see cref="Offset"/> + 284. Que os 5162 blocos do system.pak tenham 5162 valores
    /// distintos era a pista, e passou muito tempo a ser lido como ruido. Ver
    /// <see cref="XipArchive.OffsetDosDados"/>.
    /// </summary>
    public required uint OffsetDados { get; init; }

    /// <summary>O indice do par de chaves RSA e' o byte baixo do offset dos dados.</summary>
    public byte IndiceChave => (byte)OffsetDados;

    /// <summary>Onde entra na chave o XOR do descritor. Vem da posicao do bloco na lista.</summary>
    public required int XorDescritor { get; init; }

    public required int TamanhoCifrado { get; init; }
    public required int TamanhoRsa { get; init; }

    public override string ToString() =>
        $"{Nome} ({TamanhoFinal} bytes, block {TamanhoBloco} @ {Offset})";
}

/// <summary>
/// Leitura e escrita dos .pak do DJMAX (formato XIP2).
///
/// ESTRUTURA
///   0..3     "XIP2"
///   4..13    cabecalho, com os campos baralhados byte a byte
///   14..45   32 bytes fixos (uma frase em coreano que o empacotador original la' deixou)
///   46..     os blocos, um a seguir ao outro
///            + 24 bytes cifrados algures no meio ou no fim ("secreto"), que a travessia salta
///
/// CABECALHO  o offset do bloco secreto esta' nos bytes 4, 6, 8 e 11 (por essa ordem, do menos
///   significativo para o mais); o tamanho a saltar nos bytes 9 e 7. Decifrado com a chave 12,
///   o bloco secreto da' 12 bytes: <c>u16 ? | u32 inicio | u32 nFicheiros | u8 ? | u8 ?</c>.
///
/// BLOCO  284 bytes de descritor, cifrados por XOR com uma chave de 256 bytes comecando em
///   <c>(nFicheiros - indice) &amp; 255</c>, seguidos de 8 bytes com os tamanhos (esses em claro,
///   tambem baralhados), do inicio do fluxo comprimido cifrado com RSA, e do resto em claro.
///
/// O QUE FALTA  nada que impeca escrever um .pak que o jogo aceite. Os unicos campos sem
///   explicacao sao dois inteiros do bloco secreto, iguais em todos os .pak do jogo. O campo
///   de +280 de cada bloco, que aqui se leu como ruido, e' o offset dos dados desse bloco.
/// </summary>
public sealed class XipArchive
{
    /// <summary>
    /// Onde comeca a lista de blocos: <b>45 + numero de ficheiros</b>.
    ///
    /// Nao e' capricho — e' a regra, e vale nos quatro .pak do jogo que se mediram: 1 ficheiro
    /// da' 46, 34 dao 79, 284 dao 329 e 5162 dao 5207. Entre o cabecalho e os blocos ha' um
    /// byte por ficheiro de enchimento e, logo antes do primeiro bloco, uma frase de 31 bytes.
    ///
    /// ISTO PARTIU UM .PAK DE TRES FICHEIROS. Enquanto so' se escreveram .pak de um ficheiro o
    /// erro nao aparecia: 45+1 = 46, que era o valor fixo que aqui estava. Com tres, os blocos
    /// tinham de comecar em 48 e comecavam em 46 — e o cliente, que nao le' este numero do
    /// bloco secreto como o extractor le', ficava dois bytes atras do sitio certo.
    /// </summary>
    public static int InicioDosBlocos(int nFicheiros) => 45 + nFicheiros;

    /// <summary>
    /// Antes de QUE bloco vai o bloco secreto: <b>42 % nFicheiros</b>, e no FIM quando isso
    /// da' zero.
    ///
    /// O escritor punha-o sempre no fim, e isso so' esta' certo quando <c>42 % n == 0</c> —
    /// ou seja com 1, 2, 3, 6, 7, 14, 21 ou 42 ficheiros. Fora desses casos o cliente, que
    /// so' o salta quando a travessia lhe cai exatamente em cima, lia o secreto como se fosse
    /// um descritor e perdia-se. E' a explicacao de porque os .pak de um ficheiro sempre
    /// funcionaram e os de varios nao.
    ///
    /// MEDIDO em 37 .pak reais das duas geracoes (2007 e 2019), incluindo os dois crc.pak:
    /// 34 batem exatamente. Os tres que nao (system_0008 com 31, system_0009 com 56 e o
    /// crc.pak de 284) tem ENTRADAS APAGADAS — a marca <c>0x80</c> na tabela por ficheiro que
    /// o cliente zera — e essas nao aparecem na listagem, por isso o indice impresso vem
    /// deslocado. A regra e' sobre o indice FISICO do bloco.
    /// </summary>
    public static int PosicaoDoSecreto(int nFicheiros) => nFicheiros <= 0 ? 0 : 42 % nFicheiros;

    /// <summary>
    /// Os quatro bytes em +280 do descritor NAO sao uma semente com "tres bytes altos de
    /// ruido", como aqui se escreveu muito tempo: sao o <b>OFFSET ABSOLUTO DOS DADOS</b> do
    /// bloco dentro do ficheiro — ou seja <c>offset do bloco + 284</c>, que e' logo a seguir ao
    /// descritor.
    ///
    /// Medido em <b>5561 blocos</b> de seis .pak reais das duas geracoes (o system.pak com
    /// 5162, o crc.pak com 284, e mais quatro de patch): batem TODOS, sem uma excepcao.
    ///
    /// O BYTE BAIXO deste campo e' tambem o indice do par de chaves RSA que cifra o inicio do
    /// bloco — ver <see cref="IndiceDaChave"/>. E' por isso que a regra "(offset + 28) % 256"
    /// tambem funciona: <c>284 % 256 = 28</c>. Sao a mesma coisa vista de dois lados.
    ///
    /// E' ISTO QUE PARTIA OS .PAK DE VARIOS FICHEIROS. O escritor gravava sempre o mesmo
    /// <c>0x0000014A</c>, que e' exatamente <c>46 + 284</c> — o valor certo para o unico bloco
    /// de um .pak de UM ficheiro, e errado para tudo o resto.
    /// </summary>
    public static uint OffsetDosDados(int offsetDoBloco) =>
        (uint)(offsetDoBloco + XipFormat.TamanhoDescritor);

    /// <summary>
    /// Qual dos 256 pares de chaves RSA cifra o inicio do bloco: o byte baixo do
    /// <see cref="OffsetDosDados"/>.
    /// </summary>
    public static byte IndiceDaChave(int offsetDoBloco) => (byte)OffsetDosDados(offsetDoBloco);

    /// <summary>
    /// O ultimo byte do bloco secreto e' um CHECKSUM do proprio cabecalho, e o cliente
    /// RECUSA o .pak se nao bater (<c>FUN_004af870</c>, logo a seguir a decifrar o secreto).
    ///
    /// Escrevia-se aqui 255 fixo, que e' o que os .pak de patch do jogo tem — mas eles tem-no
    /// porque so' levam UM ficheiro: com n=1 e inicio=46, o XOR da' zero, o produto da' zero, e
    /// o complemento da' 255. Certo por acidente. Ao primeiro .pak com tres ficheiros o
    /// numero deixou de bater e o cliente pos o .pak de lado.
    ///
    /// E nao ha' aviso nenhum: quem chama o carregador (<c>FUN_004b7790</c>) DEITA FORA o
    /// resultado, por isso o jogo arranca na mesma — sem esse .pak e sem nenhum dos que vinham
    /// a seguir, porque a travessia para no primeiro que falha. Era esse o sintoma: um .pak
    /// mal formado levava atras o seguinte.
    ///
    /// Confirmado nos oito .pak do jogo (1, 34, 284, 343, 472 e 5162 ficheiros).
    /// </summary>
    public static byte ChecksumDoSecreto(int nFicheiros, int inicio, byte marca) =>
        (byte)~(((marca != 0 ? 1 : 0) ^ (nFicheiros & 0xFF)) * (inicio & 0xFF));

    /// <summary>
    /// A frase que fica nos 31 bytes imediatamente antes do primeiro bloco. Em cp949 le'-se
    /// "건들지좀마 제발 ㅠ ..." — "nao mexas nisto, por favor", deixada la' por quem escreveu o
    /// empacotador original. Cada .pak do jogo tem a sua; usa-se a dos .pak de patch.
    /// </summary>
    private static readonly byte[] Assinatura =
    {
        0xB0, 0xC7, 0xB5, 0xE9, 0xC1, 0xF6, 0xC1, 0xBB, 0xB8, 0xB6, 0x20, 0xC1, 0xA6, 0xB9, 0xDF,
        0x20, 0xA4, 0xD0, 0x20, 0xA4, 0xB1, 0xA4, 0xD0, 0xA4, 0xBE, 0xA1, 0xE4, 0x33, 0xA4, 0xB8, 0x00,
    };

    /// <summary>
    /// O enchimento que antecede a assinatura, um byte por ficheiro. O conteudo nao e' o mesmo
    /// em todos os .pak do jogo — o que se repete e' o COMPRIMENTO — por isso presume-se
    /// inerte; copia-se o principio do que o system.pak tem, e repete-se se for preciso mais.
    /// </summary>
    private static readonly byte[] Enchimento =
    {
        0x61, 0x6C, 0x56, 0x2E, 0x52, 0x10, 0x49, 0x71, 0x71, 0x3B, 0x69, 0x6B, 0x33, 0x26, 0x5B, 0x3C,
        0x07, 0x0C, 0x3E, 0x19, 0x24, 0x5E, 0x0D, 0x1C, 0x06, 0x37, 0x47, 0x5E, 0x33, 0x12, 0x4D, 0x48,
        0x43, 0x3B, 0x0B, 0x26, 0x1F, 0x03, 0x5A, 0x7D, 0x09, 0x38, 0x25, 0x1F, 0x5D, 0x54, 0x4B, 0x7C,
        0x16, 0x75, 0x45, 0x3B, 0x13, 0x0D, 0x09, 0x0A, 0x1C, 0x5B, 0x2E, 0x32, 0x20, 0x1A, 0x50, 0x6E,
    };

    private readonly byte[] _dados;
    private readonly XipKeys _chaves;

    public IReadOnlyList<XipEntry> Entradas { get; }

    /// <summary>Onde esta' o bloco secreto e quanto ocupa.</summary>
    public int OffsetSecreto { get; }
    public int SaltoSecreto { get; }

    private XipArchive(byte[] dados, XipKeys chaves, List<XipEntry> entradas, int offSecreto, int salto)
    {
        _dados = dados;
        _chaves = chaves;
        Entradas = entradas;
        OffsetSecreto = offSecreto;
        SaltoSecreto = salto;
    }

    // ------------------------------------------------------------------ leitura

    public static XipArchive Abrir(string caminho, XipKeys chaves)
    {
        var dados = File.ReadAllBytes(caminho);
        if (dados.Length < 64 || Encoding.ASCII.GetString(dados, 0, 4) != "XIP2")
            throw new InvalidDataException($"{Path.GetFileName(caminho)} nao e' um XIP2");

        int offSecreto = dados[4] | (dados[6] << 8) | (dados[8] << 16) | (dados[11] << 24);
        int salto = dados[9] | (dados[7] << 8);

        var secreto = chaves.Decifrar(dados.AsSpan(offSecreto, XipFormat.TamanhoSecreto),
                                      XipFormat.ChaveDoSecreto);
        int inicio = BitConverter.ToInt32(secreto, 2);
        int nFicheiros = BitConverter.ToInt32(secreto, 6);
        if (nFicheiros < 0 || nFicheiros > 100000)
            throw new InvalidDataException($"the .pak claims {nFicheiros} files — wrong keys?");

        var entradas = new List<XipEntry>(nFicheiros);
        int off = inicio;
        for (int i = 0; i < nFicheiros; i++)
        {
            if (off == offSecreto) off += salto;

            int xor = (nFicheiros - i) & 255;
            var desc = XipFormat.XorDescritor(dados.AsSpan(off, XipFormat.TamanhoDescritor), xor);

            int fimNome = Array.IndexOf(desc, (byte)0, 12, XipFormat.TamanhoNome);
            if (fimNome < 0) fimNome = 12 + XipFormat.TamanhoNome;

            uint a1 = BitConverter.ToUInt32(dados, off + 284);
            uint a2 = BitConverter.ToUInt32(dados, off + 288);
            var (cifrado, rsa) = DesbaralharTamanhos(a1, a2);

            entradas.Add(new XipEntry
            {
                Nome = Texto.GetString(desc, 12, fimNome - 12),
                Offset = off,
                TamanhoBloco = BitConverter.ToInt32(desc, 0),
                TamanhoFinal = BitConverter.ToInt32(desc, 4),
                Dispersao = BitConverter.ToUInt32(desc, 8),
                Crc32 = BitConverter.ToUInt32(desc, 272),
                SomaBytes = BitConverter.ToUInt32(desc, 276),
                OffsetDados = BitConverter.ToUInt32(desc, 280),
                XorDescritor = xor,
                TamanhoCifrado = cifrado,
                TamanhoRsa = rsa,
            });
            off += XipFormat.TamanhoDescritor + entradas[^1].TamanhoBloco;
        }
        return new XipArchive(dados, chaves, entradas, offSecreto, salto);
    }

    /// <summary>
    /// Tira o ficheiro para fora: decifra o inicio, descomprime, e desfaz a mascara se a
    /// extensao a tiver. A camada extra dos videos (.vc/.vce/.vci) NAO e' desfeita — o CRC do
    /// descritor e' anterior a ela, e o que interessa aqui e' poder voltar a empacotar.
    /// </summary>
    public byte[] Ler(XipEntry e)
    {
        var claro = _chaves.Decifrar(
            _dados.AsSpan(e.Offset + 292, e.TamanhoRsa * 2), e.IndiceChave);

        int offCauda = e.Offset + 292 + e.TamanhoCifrado;
        int tamCauda = e.TamanhoBloco - 8 - e.TamanhoCifrado;

        var comprimido = new byte[claro.Length + tamCauda];
        claro.CopyTo(comprimido, 0);
        _dados.AsSpan(offCauda, tamCauda).CopyTo(comprimido.AsSpan(claro.Length));

        var cru = Lzo1x.Descomprimir(comprimido, e.TamanhoFinal, out _);
        return XipFormat.EMascarado(e.Nome) ? XipFormat.DesmascararTexto(cru) : cru;
    }

    /// <summary>Confere o que o descritor promete contra o que saiu.</summary>
    public bool Conferir(XipEntry e, out string queixa)
    {
        try
        {
            var dados = Ler(e);
            if (XipFormat.Crc32(dados) != e.Crc32) { queixa = "crc32 mismatch"; return false; }
            if (XipFormat.Soma(dados) != e.SomaBytes) { queixa = "byte sum mismatch"; return false; }
            if (XipFormat.DispersaoNome(Texto.GetBytes(e.Nome)) != e.Dispersao)
            { queixa = "name hash mismatch"; return false; }
            queixa = "";
            return true;
        }
        catch (Exception ex)
        {
            queixa = ex.Message;
            return false;
        }
    }

    public XipEntry? Procurar(string nome) =>
        Entradas.FirstOrDefault(x => string.Equals(x.Nome, nome, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ escrita

    /// <summary>
    /// Escreve um .pak novo com os ficheiros dados. Os nomes sao os caminhos internos, com
    /// barras invertidas.
    ///
    /// Nada aqui e' escolha: o campo de +280 de cada bloco e' o offset dos dados desse bloco,
    /// calculado a' medida que se escreve — ver <see cref="OffsetDosDados"/>.
    /// </summary>
    public static void Escrever(string destino, IEnumerable<(string Nome, byte[] Conteudo)> ficheiros,
                                XipKeys chaves)
    {
        var lista = ficheiros.ToList();
        if (lista.Count == 0) throw new ArgumentException("an empty .pak is of no use");

        int inicio = InicioDosBlocos(lista.Count);

        // ONDE VAI O BLOCO SECRETO. A travessia do cliente so' o salta quando cai exatamente
        // em cima dele, por isso tem de ficar numa fronteira de bloco — mas NAO e' uma
        // fronteira qualquer: e' a do bloco 42 % nFicheiros, e so' vai para o fim quando essa
        // conta da' zero. Ver PosicaoDoSecreto.
        int posSecreto = PosicaoDoSecreto(lista.Count);
        int antesDoSecreto = posSecreto == 0 ? lista.Count : posSecreto;

        // Os blocos montam-se POR ORDEM porque o indice da chave de cada um sai do sitio onde
        // ele vai ficar no ficheiro — ver IndiceDaChave. O tamanho de um bloco nao depende da
        // chave, mas o offset do seguinte depende do tamanho do anterior, por isso nao ha'
        // atalho: e' mesmo um de cada vez.
        var blocos = new List<byte[]>(lista.Count);
        int pos = inicio;
        int offSecreto = -1;
        for (int i = 0; i < lista.Count; i++)
        {
            if (i == antesDoSecreto) { offSecreto = pos; pos += XipFormat.TamanhoSecreto; }
            int xor = (lista.Count - i) & 255;
            var b = MontarBloco(lista[i].Nome, lista[i].Conteudo, chaves, OffsetDosDados(pos), xor);
            blocos.Add(b);
            pos += b.Length;
        }
        if (offSecreto < 0) offSecreto = pos;      // 42 % n deu 0: o secreto vai para o fim

        var claro = new byte[12];
        BitConverter.TryWriteBytes(claro.AsSpan(0), (ushort)0x1102);
        BitConverter.TryWriteBytes(claro.AsSpan(2), inicio);
        BitConverter.TryWriteBytes(claro.AsSpan(6), lista.Count);
        claro[10] = 1;
        claro[11] = ChecksumDoSecreto(lista.Count, inicio, claro[10]);
        var secreto = chaves.Cifrar(claro, XipFormat.ChaveDoSecreto);

        var saida = new byte[inicio + blocos.Sum(b => b.Length) + secreto.Length];
        Encoding.ASCII.GetBytes("XIP2").CopyTo(saida, 0);
        saida[4] = (byte)offSecreto;
        saida[5] = 0x22;                              // igual em todos os .pak do jogo
        saida[6] = (byte)(offSecreto >> 8);
        saida[7] = (byte)(XipFormat.TamanhoSecreto >> 8);
        saida[8] = (byte)(offSecreto >> 16);
        saida[9] = (byte)XipFormat.TamanhoSecreto;
        saida[10] = 0;
        saida[11] = (byte)(offSecreto >> 24);
        saida[12] = 1;
        saida[13] = 0;
        for (int i = 14; i < inicio - Assinatura.Length; i++)
            saida[i] = Enchimento[(i - 14) % Enchimento.Length];
        Assinatura.CopyTo(saida, inicio - Assinatura.Length);

        int op = inicio;
        for (int i = 0; i < antesDoSecreto; i++) { blocos[i].CopyTo(saida, op); op += blocos[i].Length; }
        secreto.CopyTo(saida, op); op += secreto.Length;
        for (int i = antesDoSecreto; i < blocos.Count; i++) { blocos[i].CopyTo(saida, op); op += blocos[i].Length; }

        File.WriteAllBytes(destino, saida);
    }

    private static byte[] MontarBloco(string nome, byte[] conteudo, XipKeys chaves, uint offsetDados, int xor)
    {
        var nomeBytes = Texto.GetBytes(nome);
        if (nomeBytes.Length >= XipFormat.TamanhoNome)
            throw new ArgumentException($"o caminho '{nome}' nao cabe nos {XipFormat.TamanhoNome} bytes do descritor");

        // O que fica guardado e' o conteudo MASCARADO; o CRC e a soma sao do conteudo legivel.
        var guardado = XipFormat.EMascarado(nome) ? XipFormat.MascararTexto(conteudo) : conteudo;
        var comprimido = Lzo1x.Comprimir(guardado);

        // Confirma que o que se escreveu volta a sair igual. Um .pak que o jogo nao consiga
        // ler so' da' por si quando o jogo arranca — mais vale rebentar aqui.
        var volta = Lzo1x.Descomprimir(comprimido, guardado.Length, out int consumidos);
        if (consumidos != comprimido.Length || !volta.AsSpan().SequenceEqual(guardado))
            throw new InvalidOperationException($"compression of '{nome}' does not round-trip");

        int rsa = XipFormat.TamanhoRsa(comprimido.Length);
        int cifradoTam = rsa * 2;
        var cifrado = chaves.Cifrar(comprimido.AsSpan(0, rsa), (byte)offsetDados);
        int tamCauda = comprimido.Length - rsa;
        int tamBloco = 8 + cifradoTam + tamCauda;

        var desc = new byte[XipFormat.TamanhoDescritor];
        Array.Fill(desc, (byte)0xCC, 12, XipFormat.TamanhoNome);   // enchimento do campo do nome
        BitConverter.TryWriteBytes(desc.AsSpan(0), tamBloco);
        BitConverter.TryWriteBytes(desc.AsSpan(4), conteudo.Length);
        BitConverter.TryWriteBytes(desc.AsSpan(8), XipFormat.DispersaoNome(nomeBytes));
        nomeBytes.CopyTo(desc, 12);
        desc[12 + nomeBytes.Length] = 0;
        BitConverter.TryWriteBytes(desc.AsSpan(272), XipFormat.Crc32(conteudo));
        BitConverter.TryWriteBytes(desc.AsSpan(276), XipFormat.Soma(conteudo));
        BitConverter.TryWriteBytes(desc.AsSpan(280), offsetDados);

        var bloco = new byte[XipFormat.TamanhoDescritor + tamBloco];
        XipFormat.XorDescritor(desc, xor).CopyTo(bloco, 0);
        BaralharTamanhos(cifradoTam, rsa).CopyTo(bloco, XipFormat.TamanhoDescritor);
        cifrado.CopyTo(bloco, 292);
        comprimido.AsSpan(rsa).CopyTo(bloco.AsSpan(292 + cifradoTam));
        return bloco;
    }

    // ------------------------------------------------------------------ os 8 bytes dos tamanhos

    // Os dois tamanhos vao com os bytes trocados entre si. Nao e' cifra — e' so' para nao
    // saltarem a' vista de quem olhe para o ficheiro em bruto.

    private static (int Cifrado, int Rsa) DesbaralharTamanhos(uint a1, uint a2)
    {
        int cifrado = (int)((((a2 >> 8) & 255) << 24) | ((a1 & 255) << 16) |
                            ((a1 >> 24) << 8) | ((a1 >> 8) & 255));
        int rsa = (int)((((a1 >> 16) & 255) << 24) | (((a2 >> 24) & 255) << 16) |
                        ((a2 & 255) << 8) | ((a2 >> 16) & 255));
        return (cifrado, rsa);
    }

    private static byte[] BaralharTamanhos(int cifrado, int rsa)
    {
        var b = new byte[8];
        b[0] = (byte)(cifrado >> 16);   // a1 byte 0
        b[1] = (byte)cifrado;           // a1 byte 1
        b[2] = (byte)(rsa >> 24);       // a1 byte 2
        b[3] = (byte)(cifrado >> 8);    // a1 byte 3
        b[4] = (byte)(rsa >> 8);        // a2 byte 0
        b[5] = (byte)(cifrado >> 24);   // a2 byte 1
        b[6] = (byte)rsa;               // a2 byte 2
        b[7] = (byte)(rsa >> 16);       // a2 byte 3
        return b;
    }

    /// <summary>
    /// Os nomes dentro do .pak estao em cp949 (coreano) — o formato e' coreano, mesmo neste
    /// cliente chines. Nenhum dos 5162 nomes do system.pak sai de ASCII, mas a codificacao
    /// certa evita surpresas se algum dia sair.
    /// </summary>
    private static Encoding Texto => _texto ??= ObterTexto();
    private static Encoding? _texto;

    private static Encoding ObterTexto()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(949); }
        catch { return Encoding.ASCII; }
    }
}
