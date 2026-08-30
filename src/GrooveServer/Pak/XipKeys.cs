using System.Numerics;

namespace GrooveServer.Pak;

/// <summary>
/// As chaves com que o cliente decifra o inicio de cada bloco de um .pak.
///
/// Sao duas tabelas de 256 pares de 64 bits: <see cref="Modulos"/> (o <c>n</c>) e
/// <see cref="Expoentes"/> (o <c>e</c>). Decifrar e' <c>m = c^e mod n</c> sobre blocos de 8
/// bytes que dao 4 bytes de texto em claro; o indice da chave anda +1 (mod 256) a cada bloco.
/// E' RSA de brincar: o modulo tem 55 bits e fatoriza-se num piscar de olhos, o que e'
/// precisamente o que permite CIFRAR — sem os fatores nao havia expoente privado e nao se
/// podia escrever um .pak novo.
///
/// AS CHAVES NAO ESTAO AQUI NEM PODEM ESTAR. Sao dados do cliente, e o cliente esta'
/// empacotado com ASProtect: no DJMax.exe em disco nao aparecem, so' aparecem depois de o
/// protector descomprimir tudo em memoria. Ha' duas maneiras de as obter, ambas em
/// <see cref="Extrair"/>: de um despejo do processo (o que o <c>procdump</c> ja' produz) ou
/// de uma pasta onde ja' tenham sido guardadas.
/// </summary>
public sealed class XipKeys
{
    /// <summary>Enderecos das tabelas na imagem do cliente (base 0x400000).</summary>
    public const long EnderecoModulos = 0x0055BEB0;   // key1a_ch
    public const long EnderecoExpoentes = 0x0055B6B0; // key1b_ch
    public const int TamanhoTabela = 2048;            // 256 * 8

    public ulong[] Modulos { get; }
    public ulong[] Expoentes { get; }

    private readonly ulong[] _privados = new ulong[256];
    private readonly bool[] _temPrivado = new bool[256];

    private XipKeys(ulong[] modulos, ulong[] expoentes)
    {
        Modulos = modulos;
        Expoentes = expoentes;
    }

    public static XipKeys Carregar(string pasta)
    {
        var a = Path.Combine(pasta, "key1a_ch.bin");
        var b = Path.Combine(pasta, "key1b_ch.bin");
        if (!File.Exists(a) || !File.Exists(b))
            throw new FileNotFoundException(
                $"the keys are missing from {pasta} (key1a_ch.bin and key1b_ch.bin). " +
                "Extract them with `pak keys <dump.bin>` — see Pak/XipKeys.cs.");
        return De(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    public static XipKeys De(byte[] modulos, byte[] expoentes)
    {
        if (modulos.Length < TamanhoTabela || expoentes.Length < TamanhoTabela)
            throw new InvalidDataException("the key tables must be 2048 bytes");
        var n = new ulong[256];
        var e = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            n[i] = BitConverter.ToUInt64(modulos, i * 8);
            e[i] = BitConverter.ToUInt64(expoentes, i * 8);
        }
        return new XipKeys(n, e);
    }

    /// <summary>
    /// Tira as duas tabelas de um despejo da memoria do cliente, pelos enderecos conhecidos.
    /// O despejo e' o que o <c>procdump</c> escreve: a imagem a partir de 0x400000.
    /// </summary>
    public static (byte[] Modulos, byte[] Expoentes) Extrair(string despejo, long baseImagem = 0x400000)
    {
        using var f = File.OpenRead(despejo);

        // TEM DE SER UMA IMAGEM PLANA, nao um minidump.
        //
        // Le'-se `endereco - baseImagem` como posicao no ficheiro, o que so' vale se o despejo
        // for a imagem do processo copiada tal e qual a partir de 0x400000 — comeca no cabecalho
        // PE, portanto em "MZ". O ProcDump da Sysinternals escreve um MINIDUMP ("MDMP"), que e'
        // um contentor com streams e nao mapeia enderecos linearmente: as leituras caem em sitio
        // nenhum e as tabelas saem a zeros.
        //
        // Sem esta verificacao o erro so' aparecia la' a' frente, na fatorizacao, e de maneira
        // que nao ajudava nada a perceber a causa.
        var assinatura = new byte[4];
        if (f.Read(assinatura, 0, 4) == 4 && assinatura[0] == 'M' && assinatura[1] == 'D'
            && assinatura[2] == 'M' && assinatura[3] == 'P')
            throw new InvalidDataException(
                $"{Path.GetFileName(despejo)} is a MINIDUMP (MDMP), not a flat image. " +
                "Sysinternals ProcDump writes minidumps; this needs the process image copied " +
                "as-is from 0x400000, which starts with \"MZ\". Use `dump` to make one.");
        f.Seek(0, SeekOrigin.Begin);

        byte[] Ler(long endereco)
        {
            var buf = new byte[TamanhoTabela];
            f.Seek(endereco - baseImagem, SeekOrigin.Begin);
            if (f.Read(buf, 0, buf.Length) != buf.Length)
                throw new InvalidDataException($"o despejo acaba antes de {endereco:X8}");
            return buf;
        }
        var (m, e) = (Ler(EnderecoModulos), Ler(EnderecoExpoentes));
        Conferir(m, e);
        return (m, e);
    }

    // ------------------------------------------------------------------ decifrar/cifrar

    /// <summary>8 bytes de entrada dao 4 de saida.</summary>
    public byte[] Decifrar(ReadOnlySpan<byte> cifrado, int indice)
    {
        if (cifrado.Length % 8 != 0) throw new ArgumentException("o cifrado tem de ser multiplo de 8");
        var saida = new byte[cifrado.Length / 8 * 4];
        for (int i = 0; i < cifrado.Length / 8; i++)
        {
            ulong c = BitConverter.ToUInt64(cifrado.Slice(i * 8, 8));
            ulong m = PowMod(c, Expoentes[indice], Modulos[indice]);
            if (m > uint.MaxValue)
                throw new InvalidDataException($"decryption gave {m}, which does not fit in 32 bits — wrong keys?");
            BitConverter.TryWriteBytes(saida.AsSpan(i * 4), (uint)m);
            indice = (indice + 1) & 255;
        }
        return saida;
    }

    /// <summary>4 bytes de entrada dao 8 de saida. O inverso de <see cref="Decifrar"/>.</summary>
    public byte[] Cifrar(ReadOnlySpan<byte> claro, int indice)
    {
        if (claro.Length % 4 != 0) throw new ArgumentException("o claro tem de ser multiplo de 4");
        var saida = new byte[claro.Length / 4 * 8];
        for (int i = 0; i < claro.Length / 4; i++)
        {
            uint m = BitConverter.ToUInt32(claro.Slice(i * 4, 4));
            ulong c = PowMod(m, Privado(indice), Modulos[indice]);
            BitConverter.TryWriteBytes(saida.AsSpan(i * 8), c);
            indice = (indice + 1) & 255;
        }
        return saida;
    }

    /// <summary>
    /// O expoente privado do indice, fatorizando o modulo. Fica em cache: sao 256 numeros de
    /// 55 bits e o rho do Pollard resolve cada um em milissegundos.
    /// </summary>
    public ulong Privado(int indice)
    {
        if (_temPrivado[indice]) return _privados[indice];

        ulong n = Modulos[indice], e = Expoentes[indice];
        var fatores = Fatorizar(n);
        BigInteger lambda = 1;
        foreach (var p in fatores.Distinct())
            lambda = BigInteger.Abs(lambda * (p - 1)) / BigInteger.GreatestCommonDivisor(lambda, p - 1);

        var d = ModInverso(e, lambda);
        if (d is null)
            throw new InvalidOperationException($"key {indice}: the exponent is not invertible");

        _privados[indice] = (ulong)d.Value;
        _temPrivado[indice] = true;

        // Confirma com um valor de teste: se a cifra nao voltar atras, mais vale rebentar
        // aqui do que escrever um .pak que o jogo recusa.
        const uint teste = 0x12345678;
        if (PowMod(PowMod(teste, _privados[indice], n), e, n) != teste)
            throw new InvalidOperationException($"key {indice}: encryption is not the inverse of decryption");
        return _privados[indice];
    }

    private static BigInteger? ModInverso(BigInteger a, BigInteger m)
    {
        BigInteger g = m, x = 0, x1 = 1, a1 = a % m;
        while (a1 != 0)
        {
            var q = g / a1;
            (g, a1) = (a1, g - q * a1);
            (x, x1) = (x1, x - q * x1);
        }
        return g != 1 ? null : ((x % m) + m) % m;
    }

    // ------------------------------------------------------------------ aritmetica

    private static ulong MulMod(ulong a, ulong b, ulong m) => (ulong)((UInt128)a * b % m);

    private static ulong PowMod(ulong b, ulong e, ulong m)
    {
        ulong r = 1;
        b %= m;
        while (e > 0)
        {
            if ((e & 1) != 0) r = MulMod(r, b, m);
            b = MulMod(b, b, m);
            e >>= 1;
        }
        return r;
    }

    private static readonly ulong[] TestemunhasMiller =
        { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 };

    private static bool Primo(ulong n)
    {
        if (n < 2) return false;
        foreach (var p in TestemunhasMiller)
        {
            if (n % p == 0) return n == p;
        }
        ulong d = n - 1;
        int r = 0;
        while ((d & 1) == 0) { d >>= 1; r++; }
        foreach (var a in TestemunhasMiller)
        {
            ulong x = PowMod(a, d, n);
            if (x == 1 || x == n - 1) continue;
            bool passou = false;
            for (int i = 0; i < r - 1 && !passou; i++)
            {
                x = MulMod(x, x, n);
                if (x == n - 1) passou = true;
            }
            if (!passou) return false;
        }
        return true;
    }

    private static ulong Rho(ulong n, Random rnd)
    {
        if ((n & 1) == 0) return 2;
        while (true)
        {
            ulong x = (ulong)rnd.NextInt64(2, long.MaxValue) % n;
            ulong y = x, c = (ulong)rnd.NextInt64(1, long.MaxValue) % n, d = 1;
            while (d == 1)
            {
                x = (MulMod(x, x, n) + c) % n;
                y = (MulMod(y, y, n) + c) % n;
                y = (MulMod(y, y, n) + c) % n;
                d = Mdc(x > y ? x - y : y - x, n);
            }
            if (d != n) return d;
        }
    }

    private static ulong Mdc(ulong a, ulong b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    /// <summary>
    /// Fatoriza, e RECUSA-SE A ENTRAR EM CICLO com lixo.
    ///
    /// Isto rebentou o processo com um StackOverflow, que em .NET nem se apanha: um despejo lido
    /// do sitio errado dava tabelas a zero, e `Fatorizar(0)` recorria para sempre — o `Rho(0)`
    /// devolve 2 por 0 ser par, `0 / 2` volta a ser 0, e o `Ir` chamava-se a si proprio sem fim.
    ///
    /// Um numero que nao se pode fatorizar tem de dar uma excepcao que se apanhe e explique,
    /// nao matar o processo. Por isso: o 0 e o 1 saem a' cabeca, e um fator que nao reduza o
    /// problema (1 ou o proprio v) e' tratado como falha em vez de recursao.
    /// </summary>
    private static List<ulong> Fatorizar(ulong n)
    {
        if (n < 2)
            throw new InvalidDataException(
                $"cannot factorise {n} — the key tables look wrong (all zeros?)");

        var saida = new List<ulong>();
        var rnd = new Random(1);   // deterministico de proposito
        var porFazer = new Stack<ulong>();
        porFazer.Push(n);

        while (porFazer.Count > 0)
        {
            var v = porFazer.Pop();
            if (v == 1) continue;
            if (Primo(v)) { saida.Add(v); continue; }

            var f = Rho(v, rnd);
            if (f <= 1 || f >= v)
                throw new InvalidDataException(
                    $"cannot factorise {n} — the key tables look wrong");
            porFazer.Push(f);
            porFazer.Push(v / f);
        }
        saida.Sort();
        return saida;
    }

    /// <summary>
    /// As tabelas parecem tabelas de chave? Corre-se ANTES de as escrever no disco.
    ///
    /// Nao prova que sao as certas — para isso ha' o <see cref="Privado"/>, que confirma que a
    /// cifra e' o inverso da decifra — mas apanha de barato o caso comum: ler o despejo do sitio
    /// errado, que da' zeros ou o mesmo bloco duas vezes.
    /// </summary>
    public static void Conferir(byte[] modulos, byte[] expoentes)
    {
        if (modulos.Length != TamanhoTabela || expoentes.Length != TamanhoTabela)
            throw new InvalidDataException("the key tables must be 2048 bytes");

        if (modulos.AsSpan().SequenceEqual(expoentes))
            throw new InvalidDataException(
                "the two key tables came out identical — the dump was read at the wrong offset");

        if (modulos.All(b => b == 0) || expoentes.All(b => b == 0))
            throw new InvalidDataException(
                "a key table came out all zeros — the dump was read at the wrong offset");

        // Cada modulo tem de ser impar e grande: sao numeros de ~55 bits, nunca 0, 1 ou pares.
        for (int i = 0; i < 256; i++)
        {
            var m = BitConverter.ToUInt64(modulos, i * 8);
            if (m < 2 || (m & 1) == 0)
                throw new InvalidDataException(
                    $"modulus {i} is {m}, which is not a plausible key — the dump was read at the wrong offset");
        }
    }
}
