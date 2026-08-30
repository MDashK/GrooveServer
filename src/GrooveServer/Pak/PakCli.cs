using System.Text;

namespace GrooveServer.Pak;

/// <summary>
/// A linha de comandos dos .pak, sem dependencias do resto do servidor.
///
/// Vive aqui, e nao em Tools/, porque tem dois donos: o comando `pak` do GrooveServer e o
/// **reXIP**, o executavel autonomo (src/reXIP). Sao a mesma coisa; so' mudam as pastas por
/// omissao onde cada um procura as chaves e os .pak do jogo.
///
/// A RECEITA que motivou tudo isto — por a' venda na loja os itens que o lancamento chines
/// deixou de fora:
///   1. reXIP chaves &lt;despejo.bin&gt;              tira as tabelas de chave para keyFiles\
///   2. reXIP tirar system.pak "System\shop\ItemStock.csv" itens.csv
///   3. (editar o itens.csv)
///   4. reXIP criar system_0005.pak "System\shop\ItemStock.csv=items.csv"
///   5. copiar o system_0005.pak para a pasta FILES do jogo
///
/// Nao e' preciso mexer no system.pak nem no crc.pak. O cliente conta os `system*.pak` que
/// estao na pasta e carrega-os por ordem, ficando o ultimo a mandar; e a verificacao do
/// arranque percorre a LISTA do system.crc, que nao muda, e nao repara num .pak a mais.
/// Quem quiser mesmo mexer nos .pak originais tem o `crc` para acertar a lista.
/// </summary>
public sealed class PakCli
{
    /// <summary>Onde estao (ou vao ficar) o key1a_ch.bin e o key1b_ch.bin.</summary>
    public required string PastaChaves { get; init; }

    /// <summary>Pasta FILES do jogo, onde os .pak se procuram quando o caminho nao existe.</summary>
    public required string PastaJogo { get; init; }

    /// <summary>
    /// Se a <see cref="PastaJogo"/> e' MESMO a pasta do jogo, ou apenas a pasta onde o comando
    /// foi corrido.
    ///
    /// O reXIP so' sabe onde o jogo esta' se lhe disserem pelo DJMAX_FILES; sem isso usa o
    /// directorio actual, que serve para PROCURAR um .pak mas nao e' sitio nenhum para onde
    /// copiar o resultado. Dizer "copia-o para C:\qualquer\coisa" nesse caso e' mandar o
    /// utilizador fazer uma coisa errada com um caminho que so' calhou.
    /// </summary>
    public bool SabeOndeEOJogo { get; init; }

    /// <summary>Pastas extra onde procurar chaves, para o servidor poder reaproveitar as suas.</summary>
    public IReadOnlyList<string> ChavesAlternativas { get; init; } = Array.Empty<string>();

    /// <summary>Nome do executavel, so' para o texto de ajuda.</summary>
    public string Comando { get; init; } = "pak";

    public int Run(string[] args)
    {
        if (args.Length == 0) { Ajuda(); return 1; }
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                // OS NOMES PORTUGUESES CONTINUAM A VALER. Sao os que estao nos apontamentos, no
                // pak/traducao e nos scripts da cadeia de traducao; renomear sem mais partia
                // tudo isso de graca. Os ingleses e' que sao os publicados.
                case "dump": return Despejo(args.Skip(1).ToArray());
                case "keys" or "chaves": Chaves(args.Skip(1).ToArray()); return 0;
                case "list" or "listar": Listar(args.Skip(1).ToArray()); return 0;
                case "extract" or "tirar": return Tirar(args.Skip(1).ToArray());
                case "verify" or "conferir": return Conferir(args.Skip(1).ToArray());
                case "create" or "criar": return Criar(args.Skip(1).ToArray());
                case "crc": Crc(args.Skip(1).ToArray()); return 0;
                default: Ajuda(); return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    /// <summary>Onde se procuram os .pak, dito de maneira que sirva fora desta maquina.</summary>
    private string OndeSeProcura() => SabeOndeEOJogo
        ? $"Archives are looked up in {PastaJogo} when the path does not exist as given.\n"
          + "The DJMAX_FILES environment variable overrides it."
        : "Archives are looked up in the folder you run this from.\n"
          + "Set DJMAX_FILES to the game's FILES folder to reach them by name from anywhere.";

    private void Ajuda()
    {
        Console.WriteLine($"""
            {Comando} dump    <process> [output.bin]            copy the running game's image to a file
            {Comando} keys    <dump.bin>                        extract the key tables into keyFiles\
            {Comando} list    <archive.pak> [filter]            list what is inside
            {Comando} extract <archive.pak> <path> [output]     extract one file (path * extracts all)
            {Comando} verify  <archive.pak> [how many]          read back and check crc/sum/hash
            {Comando} create  <output.pak> <folder|inner=local> ...  build a new .pak
                      a FOLDER goes in whole, with names relative to it
            {Comando} crc     <crc.pak> <output.pak>            rebuild system.crc from the folder

            {OndeSeProcura()}
            """);
    }

    // ------------------------------------------------------------------ auxiliares

    private string Resolver(string caminho)
    {
        if (File.Exists(caminho)) return caminho;
        var naPasta = Path.Combine(PastaJogo, caminho);
        return File.Exists(naPasta) ? naPasta : caminho;
    }

    private XipKeys AbrirChaves()
    {
        foreach (var pasta in new[] { PastaChaves }.Concat(ChavesAlternativas))
        {
            if (File.Exists(Path.Combine(pasta, "key1a_ch.bin")) &&
                File.Exists(Path.Combine(pasta, "key1b_ch.bin")))
                return XipKeys.Carregar(pasta);
        }
        throw new FileNotFoundException(
            $"the keys are missing. Run this first:  {Comando} keys <dump.bin>\n" +
            "Dump the game while it is running; the keys only live in memory, because\n" +
            $"DJMax.exe on disk is packed with ASProtect. They will land in {PastaChaves}.");
    }

    // ------------------------------------------------------------------ chaves

    /// <summary>
    /// Copia a imagem do processo para ficheiro, que e' o que o <c>keys</c> precisa.
    ///
    /// Existe porque a receita mandava correr um `procdump` que o reXIP nao tem — e o ProcDump
    /// da Sysinternals, que e' o que qualquer pessoa tem a' mao, escreve um MINIDUMP, que este
    /// formato nao sabe ler. Ver Pak/ProcessImage.cs.
    /// </summary>
    private int Despejo(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine($"usage: {Comando} dump <process> [output.bin]");
            Console.WriteLine("     the game has to be running; the name can be partial (DJMax)");
            return 1;
        }
        var destino = args.Length > 1 ? args[1] : "dump.bin";
        var (lidos, saltados) = ProcessImage.Escrever(args[0], destino, Console.WriteLine);
        Console.WriteLine($"wrote {destino}: {lidos / 1024} KB read, {saltados / 1024} KB unmapped");
        return 0;
    }

    private void Chaves(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine($"usage: {Comando} keys <dump.bin>");
            Console.WriteLine($"     make the dump with `{Comando} dump DJMax` — it has to be the flat");
            Console.WriteLine("     process image from 0x400000, not a Sysinternals minidump");
            return;
        }
        var (modulos, expoentes) = XipKeys.Extrair(args[0]);

        // VERIFICAR ANTES DE ESCREVER. Estava ao contrario: escrevia os dois ficheiros e so'
        // depois e' que confirmava o par, portanto uma extraccao falhada deixava chaves erradas
        // no disco — que e' pior do que nao deixar nenhumas, porque parecem boas.
        XipKeys.De(modulos, expoentes).Privado(XipFormat.ChaveDoSecreto);

        Directory.CreateDirectory(PastaChaves);
        File.WriteAllBytes(Path.Combine(PastaChaves, "key1a_ch.bin"), modulos);
        File.WriteAllBytes(Path.Combine(PastaChaves, "key1b_ch.bin"), expoentes);
        Console.WriteLine($"keys written to {PastaChaves} (key1a_ch.bin, key1b_ch.bin) and verified");
    }

    // ------------------------------------------------------------------ listar / tirar

    private void Listar(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine($"usage: {Comando} list <archive.pak> [filter]"); return; }
        var pak = XipArchive.Abrir(Resolver(args[0]), AbrirChaves());
        string filtro = args.Length > 1 ? args[1] : "";

        Console.WriteLine($"{pak.Entradas.Count} file(s); secret block at {pak.OffsetSecreto}");
        long total = 0;
        int mostrados = 0;
        foreach (var e in pak.Entradas)
        {
            total += e.TamanhoFinal;
            if (filtro.Length > 0 && !e.Nome.Contains(filtro, StringComparison.OrdinalIgnoreCase)) continue;
            mostrados++;
            Console.WriteLine($"  {e.TamanhoFinal,10}  block {e.TamanhoBloco,9}  @{e.Offset,-10} " +
                              $"k={e.IndiceChave,-3} crc={e.Crc32:x8}  {e.Nome}");
        }
        Console.WriteLine($"  ({mostrados} shown, {total} bytes uncompressed in total)");
    }

    private int Tirar(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine($@"usage: {Comando} extract <archive.pak> <System\shop\ItemStock.csv> [output]");
            return 1;
        }
        var pak = XipArchive.Abrir(Resolver(args[0]), AbrirChaves());

        if (args[1] == "*")
        {
            var raiz = args.Length > 2 ? args[2] : "extraido";
            int n = 0;
            foreach (var e in pak.Entradas)
            {
                var destino = Path.Combine(raiz, e.Nome.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destino)!);
                File.WriteAllBytes(destino, pak.Ler(e));
                n++;
            }
            Console.WriteLine($"{n} file(s) into {raiz}");
            return 0;
        }

        var entrada = pak.Procurar(args[1]);
        if (entrada is null)
        {
            Console.WriteLine($"'{args[1]}' is not in this .pak.");
            foreach (var p in pak.Entradas
                         .Where(x => x.Nome.Contains(Path.GetFileName(args[1]), StringComparison.OrdinalIgnoreCase))
                         .Take(10))
                Console.WriteLine($"   maybe: {p.Nome}");
            return 1;
        }
        var fora = args.Length > 2 ? args[2] : Path.GetFileName(entrada.Nome);
        var dados = pak.Ler(entrada);
        File.WriteAllBytes(fora, dados);
        Console.WriteLine($"{entrada.Nome} -> {fora} ({dados.Length} bytes, crc {XipFormat.Crc32(dados):x8})");
        if (XipFormat.EMascarado(entrada.Nome))
            Console.WriteLine("   (it was a masked file; it came out readable and is masked again on repack)");
        return 0;
    }

    // ------------------------------------------------------------------ conferir

    private int Conferir(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine($"usage: {Comando} verify <archive.pak> [how many]"); return 1; }
        var pak = XipArchive.Abrir(Resolver(args[0]), AbrirChaves());
        int quantos = args.Length > 1 && int.TryParse(args[1], out var q) ? q : pak.Entradas.Count;

        int bons = 0, maus = 0;
        foreach (var e in pak.Entradas.Take(quantos))
        {
            if (pak.Conferir(e, out var queixa)) bons++;
            else { maus++; Console.WriteLine($"  BAD {e.Nome}: {queixa}"); }
        }
        Console.WriteLine($"{bons} good, {maus} bad out of {Math.Min(quantos, pak.Entradas.Count)}");
        return maus == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ criar

    private int Criar(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine($"""
                usage: {Comando} create <output.pak> <folder|inner=local> [...]

                  {Comando} create system_0099.pak patch\
                  {Comando} create system_0099.pak "System\shop\ItemStock.csv=items.csv"

                A FOLDER goes in whole, and the name stored in the .pak is the path relative
                to it: patch\System\shop\ItemStock.csv goes in as
                System\shop\ItemStock.csv. The two forms can be mixed.
                """);
            return 1;
        }
        var ficheiros = new List<(string Nome, byte[] Conteudo)>();
        foreach (var par in args.Skip(1))
        {
            int i = par.LastIndexOf('=');

            // SEM '=' e' uma pasta, e vai inteira. O nome que fica dentro do .pak e' o caminho
            // RELATIVO a essa pasta — e' o unico criterio sem ambiguidade, porque o nome interno
            // nao tem de ter nada a ver com o sitio do disco onde o ficheiro esta'. Para obter
            // "System\shop\ItemStock.csv" la' dentro, aponta-se a uma pasta que tenha
            // System\shop\ItemStock.csv.
            if (i < 0)
            {
                var pasta = par.TrimEnd('\\', '/');
                if (!Directory.Exists(pasta))
                {
                    Console.WriteLine($"'{par}' is neither a folder nor an <inner>=<local> pair");
                    return 1;
                }
                var achados = Directory.GetFiles(pasta, "*", SearchOption.AllDirectories)
                                       .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                       .ToList();
                if (achados.Count == 0) { Console.WriteLine($"{pasta} is empty"); return 1; }
                foreach (var f in achados)
                    ficheiros.Add((Path.GetRelativePath(pasta, f).Replace('/', '\\'), File.ReadAllBytes(f)));
                Console.WriteLine($"   {pasta}: {achados.Count} file(s)");
                continue;
            }

            var interno = par[..i].Replace('/', '\\');
            var local = par[(i + 1)..];
            if (!File.Exists(local)) { Console.WriteLine($"cannot find {local}"); return 1; }
            ficheiros.Add((interno, File.ReadAllBytes(local)));
        }

        // Nomes repetidos nao sao erro (dentro do jogo o ultimo manda), mas quase sempre sao
        // engano — e o `criar` confere o conteudo pelo nome, o que ficaria ambiguo.
        var repetidos = ficheiros.GroupBy(f => f.Nome, StringComparer.OrdinalIgnoreCase)
                                 .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (repetidos.Count > 0)
        {
            Console.WriteLine($"duplicate names: {string.Join(", ", repetidos)}");
            return 1;
        }

        var chaves = AbrirChaves();
        XipArchive.Escrever(args[0], ficheiros, chaves);

        // Volta a abrir o que se acabou de escrever e confere tudo. E' a diferenca entre saber
        // que esta' bem e esperar que esteja.
        var relido = XipArchive.Abrir(args[0], chaves);
        int maus = 0;
        foreach (var e in relido.Entradas)
        {
            var original = ficheiros.First(f => f.Nome.Equals(e.Nome, StringComparison.OrdinalIgnoreCase)).Conteudo;
            if (!relido.Ler(e).AsSpan().SequenceEqual(original) || !relido.Conferir(e, out _))
            { maus++; Console.WriteLine($"  BAD {e.Nome}"); }
        }
        var tam = new FileInfo(args[0]).Length;
        Console.WriteLine($"{args[0]}: {ficheiros.Count} file(s), {tam} bytes — " +
                          (maus == 0 ? "read back and verified, all good" : $"{maus} PROBLEM(S)"));
        if (maus == 0 && Path.GetFileName(args[0]).StartsWith("system_", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"   copy it to {(SabeOndeEOJogo ? PastaJogo : "the game's FILES folder")}" +
                              " — the client loads it over the system.pak with nothing else to do.");
        return maus == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ system.crc

    private sealed record EntradaCrc(string Nome, uint Checksum, uint Tamanho);

    /// <summary>
    /// Reescreve o <c>system.crc</c> que vive dentro do crc.pak, acertando-o aos .pak que estao
    /// mesmo na pasta. So' e' preciso se se mexer nos .pak que ja' la' estavam: o arranque do
    /// cliente percorre esta lista e para tudo assim que uma entrada nao bater.
    /// </summary>
    private void Crc(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine($"usage: {Comando} crc <crc.pak> <output.pak>"); return; }
        var chaves = AbrirChaves();
        var pak = XipArchive.Abrir(Resolver(args[0]), chaves);

        var ficheiros = new List<(string, byte[])>();
        foreach (var e in pak.Entradas)
        {
            var dados = pak.Ler(e);
            if (e.Nome.Equals("system.crc", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("system.crc antes:");
                foreach (var l in LerCrc(dados))
                    Console.WriteLine($"   {l.Nome,-18} chk={l.Checksum:x8} tam={l.Tamanho}");

                var nova = Directory.GetFiles(PastaJogo, "system*.pak")
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new EntradaCrc(Path.GetFileName(f), XipFormat.ChecksumDoPak(f),
                                                (uint)new FileInfo(f).Length))
                    .ToList();
                Console.WriteLine("system.crc depois:");
                foreach (var l in nova)
                    Console.WriteLine($"   {l.Nome,-18} chk={l.Checksum:x8} tam={l.Tamanho}");
                dados = EscreverCrc(nova);
            }
            ficheiros.Add((e.Nome, dados));
        }
        XipArchive.Escrever(args[1], ficheiros, chaves);
        Console.WriteLine($"{args[1]} escrito ({ficheiros.Count} entradas). " +
                          "Guarda o crc.pak original antes de o substituir.");
    }

    private static List<EntradaCrc> LerCrc(byte[] dados)
    {
        var saida = new List<EntradaCrc>();
        int p = 0;
        while (p + 2 <= dados.Length)
        {
            int n = BitConverter.ToUInt16(dados, p);
            p += 2;
            if (p + n + 8 > dados.Length) break;
            var nome = Encoding.ASCII.GetString(dados, p, n);
            p += n;
            saida.Add(new EntradaCrc(nome, BitConverter.ToUInt32(dados, p), BitConverter.ToUInt32(dados, p + 4)));
            p += 8;
        }
        return saida;
    }

    private static byte[] EscreverCrc(List<EntradaCrc> lista)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        foreach (var e in lista)
        {
            var nome = Encoding.ASCII.GetBytes(e.Nome);
            w.Write((ushort)nome.Length);
            w.Write(nome);
            w.Write(e.Checksum);
            w.Write(e.Tamanho);
        }
        return ms.ToArray();
    }
}
