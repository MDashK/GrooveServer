using GrooveServer.Pak;

// reXIP — le' e escreve os .pak (XIP2) do DJMAX Online.
//
// O `unxip` so' extrai. Este faz os dois lados: tira um ficheiro do system.pak, deixa-o editar,
// e volta a empacota-lo num .pak que o jogo carrega por cima do original.
//
// Nao precisa de nada do servidor — e' so' esta pasta e as duas tabelas de chave, que se tiram
// da memoria do cliente (`reXIP chaves`). O formato esta' descrito em Pak/XipArchive.cs e a
// historia toda em docs/por-fazer.md, seccao A21.

if (args.Length > 0 && (args[0] is "-h" or "--help" or "/?"))
    args = Array.Empty<string>();

// As chaves ficam ao lado do executavel; a pasta do jogo vem do ambiente ou e' a actual.
var junto = AppContext.BaseDirectory;
var jogo = Environment.GetEnvironmentVariable("DJMAX_FILES") is { Length: > 0 } v && Directory.Exists(v)
    ? v : Directory.GetCurrentDirectory();

var cli = new PakCli
{
    Comando = "reXIP",
    PastaChaves = Path.Combine(junto, "keyFiles"),
    PastaJogo = jogo,
    // So' se sabe onde o jogo esta' se o disserem pelo DJMAX_FILES; o directorio actual
    // serve para procurar um .pak, mas nao e' a pasta do jogo.
    SabeOndeEOJogo = Environment.GetEnvironmentVariable("DJMAX_FILES") is { Length: > 0 } d
                     && Directory.Exists(d),
    // Tambem se aceitam chaves na pasta de onde se corre o comando, para nao obrigar a
    // copia-las para junto do executavel.
    ChavesAlternativas = new[] { Path.Combine(Directory.GetCurrentDirectory(), "keyFiles") },
};

if (args.Length == 0)
{
    Console.WriteLine("reXIP — packer for the DJMAX Online .pak archives (XIP2 format)\n");
    cli.Run(args);
    Console.WriteLine("""

        The usual recipe:

          reXIP keys dump.bin
          reXIP extract system.pak * patch
          (edit whatever you want under patch\, delete the rest)
          reXIP create system_0005.pak patch

        A FOLDER goes in whole, keeping the names relative to it, so `patch\System\shop\`
        lands as `System\shop\`. Only put in it what you actually changed: the archive you
        build overrides those entries and nothing else.

        For a single file there is a shorthand that skips the folder:

          reXIP create system_0005.pak "System\shop\ItemStock.csv=items.csv"

        Copy the new .pak into the game's FILES folder. The client loads every `system*.pak` in
        order and the last one wins, so there is no need to touch system.pak or crc.pak — the
        startup check walks the list inside system.crc, which does not change.

        The keys only exist in the client's MEMORY (DJMax.exe on disk is packed with
        ASProtect). `keys` lifts them from a process dump, starting at 0x400000.
        """);
    return 1;
}

return cli.Run(args);
