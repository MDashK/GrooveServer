namespace GrooveServer.Net;

/// <summary>
/// Respostas ao <c>StartReq</c>, guardadas por musica.
///
/// Ao iniciar uma musica o servidor nao envia uma mensagem, envia um GRUPO:
/// <c>GameInfoInf</c> (os dados da musica), <c>JoinEventInf</c>, <c>0x00C4</c>,
/// <c>0x00C9</c>, <c>StartParameterInf</c> e <c>StartInf</c>. Varias delas trazem
/// parametros da musica, nao so' a primeira.
///
/// Substituir apenas o <c>GameInfoInf</c> e deixar as restantes da gravacao deixa o
/// cliente com dados incoerentes â€” recebe o bloco de uma musica e os parametros de
/// outra â€” e fica bloqueado sem chegar a pedir o arranque. Por isso guarda-se o grupo
/// completo.
///
/// Os corpos ficam DECIFRADOS, para poderem ser recifrados com a chave de cada sessao.
/// </summary>
public sealed class SongLibrary
{
    /// <summary>Mensagem enviada pelo servidor: cabecalho de 7 bytes + corpo em claro.</summary>
    public readonly record struct Entry(ushort Id, byte[] Header, byte[] Body);

    private readonly Dictionary<Protocol.SongKey, List<Entry>> _groups = new();
    private readonly string _dir;

    private const uint Magic = 0x314C4753;   // "SGL1"

    public SongLibrary(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(directory, "song_*.bin"))
        {
            var key = Protocol.SongKey.FromFileName(file);
            if (key is null) { Console.WriteLine($"  (ignored {Path.GetFileName(file)}: name without a difficulty)"); continue; }
            try { _groups[key.Value] = Read(file); }
            catch (Exception ex) { Console.WriteLine($"  (ignored {Path.GetFileName(file)}: {ex.Message})"); }
        }
    }

    public int Count => _groups.Count;
    public IEnumerable<Protocol.SongKey> Keys => _groups.Keys.OrderBy(k => k.Song).ThenBy(k => k.Difficulty);

    /// <summary>Grupo de resposta ao StartReq para este chart, ou null.</summary>
    public IReadOnlyList<Entry>? Get(Protocol.SongKey key) =>
        _groups.TryGetValue(key, out var g) ? g : null;

    /// <summary>
    /// O mesmo, mas descendo de dificuldade ate' encontrar alguma.
    ///
    /// **PARA QUE SERVE.** Os courses pedem charts que o free mode nao oferece — o "Yo! MAX!"
    /// pede o <c>Yo Creo Que Si</c> em HARD e essa musica nao tem separador HARD na lista.
    /// Charts assim so' se apanham jogando o proprio course, e em 7 teclas isso exige
    /// aguentar o course inteiro, que nao e' coisa que se faca a pedido.
    ///
    /// Sem recuo o servidor servia o bloco gravado — a musica ERRADA, sem aviso. Com recuo
    /// serve a mesma musica numa dificuldade mais baixa: e' mais facil do que devia ser, mas
    /// e' a musica certa, e o registo diz sempre o que aconteceu.
    ///
    /// Nunca sobe: uma dificuldade acima seria mais dificil do que o course pede, e isso
    /// mudava um course facil num impossivel.
    /// </summary>
    public (IReadOnlyList<Entry> Grupo, Protocol.SongKey Usada)? GetComRecuo(Protocol.SongKey key)
    {
        for (int d = key.Difficulty; d >= 0; d--)
        {
            var tentativa = new Protocol.SongKey(key.Song, (byte)d);
            if (_groups.TryGetValue(tentativa, out var g)) return (g, tentativa);
        }
        return null;
    }

    /// <summary>Dificuldades disponiveis para uma musica.</summary>
    public IEnumerable<byte> DifficultiesFor(uint song) =>
        _groups.Keys.Where(k => k.Song == song).Select(k => k.Difficulty).OrderBy(d => d);

    public void Add(Protocol.SongKey key, List<Entry> group)
    {
        _groups[key] = group;
        Write(Path.Combine(_dir, key.FileName), group);
    }

    private static void Write(string path, List<Entry> group)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(Magic);
        w.Write(group.Count);
        foreach (var e in group)
        {
            w.Write(e.Id);
            w.Write(e.Header.Length); w.Write(e.Header);
            w.Write(e.Body.Length);   w.Write(e.Body);
        }
    }

    private static List<Entry> Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("formato antigo; volta a correr o harvest");
        int n = r.ReadInt32();
        var list = new List<Entry>(n);
        for (int i = 0; i < n; i++)
        {
            ushort id = r.ReadUInt16();
            var header = r.ReadBytes(r.ReadInt32());
            var body = r.ReadBytes(r.ReadInt32());
            list.Add(new Entry(id, header, body));
        }
        return list;
    }

    /// <summary>
    /// Extrai os grupos de uma captura, emparelhando cada arranque com a escolha que o
    /// cliente fez imediatamente antes.
    ///
    /// A musica le-se do proprio bloco, mas a dificuldade so' existe no
    /// <c>ChangeDiscReq</c> do cliente (byte 0 do corpo cifrado). Como cada jogada e'
    /// sempre "escolher, depois arrancar", a n-esima escolha corresponde ao n-esimo
    /// arranque.
    /// </summary>
    public static int Harvest(string capturePath, ResponseMap map, string directory)
    {
        var lib = new SongLibrary(directory);
        int before = lib.Count;

        var choices = ClientChoices.AtEachStart(capturePath);
        int starts = map.OccurrencesOf(Protocol.RequestId.StartReq);
        if (choices.Count != starts)
            Console.WriteLine($"  WARNING: {choices.Count} choices for {starts} starts â€” " +
                              "the pairing may be wrong");

        for (int i = 0; i < starts; i++)
        {
            var bucket = map.For(Protocol.RequestId.StartReq, i);
            var info = bucket.FirstOrDefault(m => m.Id == Protocol.GameInfoFraming.MessageId);
            if (info.Body is null || info.Body.Length < 8) continue;

            uint song = Protocol.GameInfoFraming.ReadSongId(info.Body);
            if (i >= choices.Count) { Console.WriteLine($"  start {i}: no matching choice"); continue; }

            var choice = choices[i];
            if (choice.Song != song)
            {
                Console.WriteLine($"  start {i}: MISALIGNED â€” the choice says song {choice.Song}, " +
                                  $"the block says {song}; ignored");
                continue;
            }

            // So' existem cinco dificuldades. Um valor fora disso significa que a decifra
            // do fluxo do cliente perdeu o sincronismo — acontece quando se processa uma
            // captura que ainda esta' a ser escrita e o ultimo segmento vem truncado.
            // Rejeitar em vez de guardar impede que lixo entre na biblioteca com um nome
            // que parece legitimo.
            if (choice.Difficulty > 4)
            {
                Console.WriteLine($"  start {i}: invalid difficulty {choice.Difficulty} — " +
                                  "cipher out of sync; ignored");
                continue;
            }

            var key = new Protocol.SongKey(song, choice.Difficulty);
            var group = bucket.Select(m => new Entry(m.Id, m.Header, m.Body)).ToList();
            lib.Add(key, group);
            Console.WriteLine($"  {key,-28} {group.Sum(g => g.Body.Length),7} bytes");
        }
        return lib.Count - before;
    }
}

