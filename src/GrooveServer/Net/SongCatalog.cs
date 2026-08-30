namespace GrooveServer.Net;

/// <summary>
/// Catalogo das musicas, lido do <c>DiscStock.csv</c> do proprio jogo.
///
/// O ficheiro veio do <c>system.pak</c>, extraido com o unxip (ADHSoft/Xip-Pak-Extractor).
/// As chaves do extractor sairam do dump de memoria do cliente, porque o executavel em
/// disco esta' protegido com ASProtect.
///
/// ATENCAO A' BASE: o CSV numera as musicas a partir de 1 e a rede a partir de 0. Confirmado
/// com o Brave it out — linha 9 do CSV, musica 8 na rede — e com os dois courses que ja'
/// tinham sido lidos das capturas. Aqui guarda-se ja' o numero DA REDE.
///
/// So' serve para o servidor poder dizer "Brave_it_out" em vez de "musica 8". Nao muda nada
/// no protocolo; e' de diagnostico.
/// </summary>
public sealed class SongCatalog
{
    public readonly record struct Song(uint Id, string Titulo, string Genero, string Pak);

    private readonly Dictionary<uint, Song> _porId = new();

    public int Count => _porId.Count;

    public Song? Get(uint id) => _porId.TryGetValue(id, out var s) ? s : null;

    /// <summary>Titulo da musica, ou "musica N" se o catalogo nao a conhecer.</summary>
    public string Nome(uint id) => _porId.TryGetValue(id, out var s) ? s.Titulo : $"song {id}";

    public static SongCatalog Load(string path)
    {
        var c = new SongCatalog();
        if (!File.Exists(path)) return c;

        foreach (var linha in File.ReadAllLines(path).Skip(1))
        {
            // O CSV nao tem aspas nem virgulas dentro dos campos — um split simples chega.
            var campos = linha.Split(',');
            if (campos.Length < 14 || !uint.TryParse(campos[0], out uint idFicheiro)) continue;
            if (idFicheiro == 0) continue;

            c._porId[idFicheiro - 1] = new Song(
                idFicheiro - 1,
                campos[1].Trim(),
                campos[4].Trim(),
                campos[13].Trim());   // Tag = nome do .pak
        }
        return c;
    }
}
