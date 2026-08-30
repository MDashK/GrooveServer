using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Extrai de uma gravacao a lista de etapas de um course: musica e dificuldade de cada uma.
///
/// A MUSICA le-se do bloco (offset 4 do corpo do GameInfoInf). A DIFICULDADE nao vem em
/// lado nenhum: em course mode nao ha' <c>ChangeDiscReq</c>, porque as musicas sao fixas e
/// o jogador nao escolhe nada. Deduz-se comparando o bloco gravado com os charts que a
/// biblioteca tem para essa musica — o bloco de EASY e o de NORMAL sao ficheiros
/// diferentes, por isso o que bater identifica a dificuldade.
///
/// A comparacao IGNORA O BYTE 2. Os blocos colhidos dos ficheiros do jogo diferem do que o
/// servidor envia exatamente nesse byte, em 12 de 13 casos medidos — e so' nesse. Exigir
/// igualdade total nao reconhecia nenhum chart.
/// </summary>
public static class CourseInfo
{
    private const int ByteDois = 2;

    /// <summary>
    /// Compara o bloco da MESMA musica em duas gravacoes diferentes. Serve para saber se o
    /// bloco e' so' o chart (igual em todas as sessoes) ou se traz tambem estado da sessao.
    /// </summary>
    public static void Comparar(string a, string b)
    {
        var ga = Blocos(a); var gb = Blocos(b);
        var comuns = ga.Keys.Intersect(gb.Keys).OrderBy(x => x).ToList();
        if (comuns.Count == 0) { Console.WriteLine("nenhuma musica em comum"); return; }

        Console.WriteLine($"{Path.GetFileName(a)} vs {Path.GetFileName(b)} — {comuns.Count} musica(s) em comum\n");
        foreach (var musica in comuns)
        {
            var x = ga[musica]; var y = gb[musica];
            if (x.Length != y.Length) { Console.WriteLine($"  musica {musica,-4} tamanhos diferentes: {x.Length} vs {y.Length}"); continue; }
            int n = 0, primeiro = -1;
            for (int i = 0; i < x.Length; i++)
                if (x[i] != y[i]) { n++; if (primeiro < 0) primeiro = i; }
            Console.WriteLine(n == 0
                ? $"  musica {musica,-4} IDENTICO ({x.Length} bytes)"
                : $"  musica {musica,-4} difere em {n} de {x.Length} bytes, a partir do offset {primeiro}");
        }
    }

    /// <summary>
    /// Strings ASCII do bloco. O nome do ficheiro .pak da musica viaja aqui — e' isso que
    /// permite passar de "musica 126" para um nome que se reconhece na listagem do jogo.
    /// </summary>
    private static List<string> Strings(byte[] corpo, int minimo = 4)
    {
        var r = new List<string>();
        var actual = new System.Text.StringBuilder();
        foreach (var b in corpo)
        {
            if (b >= 0x20 && b < 0x7F) { actual.Append((char)b); continue; }
            if (actual.Length >= minimo) r.Add(actual.ToString());
            actual.Clear();
        }
        if (actual.Length >= minimo) r.Add(actual.ToString());
        return r.Distinct().ToList();
    }

    /// <summary>
    /// Compara todos os blocos de uma gravacao contra o primeiro. Feito para a captura das
    /// velocidades, onde a MESMA musica foi jogada oito vezes com definicoes diferentes: o
    /// que variar entre blocos e' o que a definicao muda.
    /// </summary>
    public static void BlocosDaGravacao(string recording)
    {
        var todos = new List<byte[]>();
        foreach (var grupo in ResponseMap.Load(recording).AllSetsContaining(GameInfoFraming.MessageId))
            foreach (var m in grupo)
                if (m.Id == GameInfoFraming.MessageId && m.Body.Length > 8) todos.Add(m.Body);

        Console.WriteLine($"{Path.GetFileName(recording)}: {todos.Count} bloco(s)\n");
        if (todos.Count == 0) return;

        var baseB = todos[0];
        for (int i = 0; i < todos.Count; i++)
        {
            var b = todos[i];

            // O prefixo sai sempre, mesmo quando os blocos tem tamanhos diferentes (musicas
            // diferentes) e a comparacao byte-a-byte nao se aplica. E' onde estao os campos
            // de estado da etapa — velocidade (+2), musica (+4), course (+10/+12/+14).
            Console.WriteLine($"  #{i + 1}: +2={b[2]} (0x{b[2]:x2})  musica={GameInfoFraming.ReadSongId(b)}" +
                              $"  {b.Length} bytes");

            if (b.Length != baseB.Length) continue;

            var difs = new List<int>();
            for (int j = 0; j < b.Length; j++) if (b[j] != baseB[j]) difs.Add(j);
            Console.WriteLine(difs.Count == 0
                ? $"  #{i + 1}: identico ao #1"
                : $"  #{i + 1}: {difs.Count} byte(s) diferentes, em {string.Join(", ", difs.Take(12))}" +
                  (difs.Count > 12 ? " ..." : ""));
            foreach (var j in difs.Take(6))
                Console.WriteLine($"        +{j}: #1={baseB[j]:x2}  #{i + 1}={b[j]:x2}");
        }
    }

    /// <summary>
    /// Todos os grupos de FECHO de musica (os que contem o 0x002A), em hexadecimal, mensagem
    /// a mensagem. E' aqui que o servidor diz ao cliente o que mostrar no ecra de resultados
    /// e o que vem a seguir; comparar as etapas de um course gravado mostra que campos
    /// avancam com a etapa.
    /// </summary>
    public static void Fechos(string recording, ushort filtro = 0, ushort ancora = 0x002A)
    {
        var grupos = ResponseMap.Load(recording).AllSetsContaining(ancora);
        Console.WriteLine($"{Path.GetFileName(recording)}: {grupos.Count} grupo(s) com 0x{ancora:x4}");
        Console.WriteLine();

        for (int i = 0; i < grupos.Count; i++)
        {
            Console.WriteLine($"--- fecho {i + 1} ---");
            foreach (var m in grupos[i])
            {
                if (filtro != 0 && m.Id != filtro) continue;
                Console.WriteLine($"  0x{m.Id:x4}  cab {Convert.ToHexString(m.Header)}");
                for (int off = 0; off < m.Body.Length; off += 32)
                    Console.WriteLine($"        +{off,-4} {Convert.ToHexString(m.Body, off, Math.Min(32, m.Body.Length - off))}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Rajada de arranque GRAVADA contra a que a biblioteca serve, para a mesma musica.
    ///
    /// O servidor serve as musicas de um course a partir da biblioteca, colhida em free
    /// mode. Se alguma mensagem da rajada trouxer estado da ETAPA, o que a biblioteca tem
    /// esta' errado por construcao — e' isto que o mostra, campo a campo.
    /// </summary>
    public static void ArranqueVsBiblioteca(string recording, SongLibrary library)
    {
        var grupos = ResponseMap.Load(recording).AllSetsContaining(GameInfoFraming.MessageId);
        Console.WriteLine($"{Path.GetFileName(recording)}: {grupos.Count} etapa(s)");
        Console.WriteLine();

        for (int i = 0; i < grupos.Count; i++)
        {
            var bloco = grupos[i].FirstOrDefault(m => m.Id == GameInfoFraming.MessageId);
            if (bloco.Body is not { Length: > 8 }) continue;
            uint musica = GameInfoFraming.ReadSongId(bloco.Body);

            var dificuldades = library.DifficultiesFor(musica).ToList();
            var grupoLib = dificuldades.Count > 0
                ? library.Get(new SongKey(musica, dificuldades[0])) : null;

            Console.WriteLine($"--- etapa {i + 1}: musica {musica} " +
                              $"(biblioteca: {(grupoLib is null ? "AUSENTE" : $"dif {dificuldades[0]}")}) ---");
            if (grupoLib is null) { Console.WriteLine(); continue; }

            foreach (var m in grupos[i])
            {
                if (m.Id == GameInfoFraming.MessageId)
                {
                    var lb = grupoLib.FirstOrDefault(e => e.Id == m.Id);
                    Console.WriteLine($"  0x{m.Id:x4}  bloco gravado {m.Body.Length}B, biblioteca {lb.Body?.Length ?? -1}B");
                    if (lb.Body is { } lbCorpo && lbCorpo.Length == m.Body.Length)
                    {
                        var difs = new List<int>();
                        for (int k = 0; k < lbCorpo.Length; k++) if (lbCorpo[k] != m.Body[k]) difs.Add(k);
                        Console.WriteLine(difs.Count == 0
                            ? "          corpos IDENTICOS"
                            : $"          corpos diferem em {difs.Count} byte(s): {string.Join(", ", difs.Take(20))}" +
                              (difs.Count > 20 ? " ..." : ""));
                        foreach (var k in difs.Take(10))
                            Console.WriteLine($"            +{k}: gravado {m.Body[k]:x2}  biblioteca {lbCorpo[k]:x2}");
                    }
                    string cabB = Convert.ToHexString(m.Header), cabB2 = Convert.ToHexString(lb.Header ?? Array.Empty<byte>());
                    Console.WriteLine($"          cab gravado {cabB}   biblioteca {cabB2}   {(cabB == cabB2 ? "=" : "DIFERE")}");
                    continue;
                }
                var lib = grupoLib.FirstOrDefault(e => e.Id == m.Id);
                if (lib.Header is null) { Console.WriteLine($"  0x{m.Id:x4}  NAO ESTA' NA BIBLIOTECA"); continue; }

                string cabG = Convert.ToHexString(m.Header), cabL = Convert.ToHexString(lib.Header);
                string corG = Convert.ToHexString(m.Body),   corL = Convert.ToHexString(lib.Body);
                Console.WriteLine($"  0x{m.Id:x4}  cab gravado {cabG}   biblioteca {cabL}   {(cabG == cabL ? "=" : "DIFERE")}");
                if (corG != corL)
                {
                    Console.WriteLine($"          corpo gravado    {corG}");
                    Console.WriteLine($"          corpo biblioteca {corL}");
                }
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Que bytes do prefixo do bloco variam ENTRE MUSICAS na biblioteca.
    ///
    /// Serve para separar o que e' da musica do que e' estado da sessao: um byte igual em
    /// todos os charts nao pode identificar a musica, logo o que la' esta' e' contexto — e
    /// contexto colhido em free mode nao serve para uma etapa de course.
    /// </summary>
    public static void PrefixosDaBiblioteca(SongLibrary library, int quantos = 12)
    {
        var chaves = library.Keys.Take(quantos).ToList();
        var prefixos = new List<(string Nome, byte[] Bytes)>();
        foreach (var k in chaves)
        {
            var b = library.Get(k)?.FirstOrDefault(e => e.Id == GameInfoFraming.MessageId);
            if (b is { Body.Length: >= GameInfoFraming.PrefixLength })
                prefixos.Add((k.ToString(), b.Value.Body.Take(GameInfoFraming.PrefixLength).ToArray()));
        }
        if (prefixos.Count < 2) { Console.WriteLine("blocos insuficientes"); return; }

        Console.WriteLine($"{prefixos.Count} charts, prefixo de {GameInfoFraming.PrefixLength} bytes");
        Console.WriteLine();
        Console.WriteLine("offset  varia?   valores");
        for (int i = 0; i < GameInfoFraming.PrefixLength; i++)
        {
            var vals = prefixos.Select(p => p.Bytes[i]).Distinct().ToList();
            string amostra = string.Join(" ", prefixos.Take(6).Select(p => p.Bytes[i].ToString("x2")));
            Console.WriteLine($"  +{i,-4} {(vals.Count > 1 ? "VARIA" : "igual"),-7} {amostra}" +
                              (vals.Count > 1 ? $"   ({vals.Count} valores distintos)" : ""));
        }
    }

    /// <summary>Prefixo do bloco gravado e o da biblioteca, lado a lado, 16 bytes por linha.</summary>
    public static void PrefixoLadoALado(string recording, SongLibrary library, int etapa = 1)
    {
        var grupos = ResponseMap.Load(recording).AllSetsContaining(GameInfoFraming.MessageId);
        if (etapa < 1 || etapa > grupos.Count) { Console.WriteLine("etapa fora do intervalo"); return; }
        var g = grupos[etapa - 1].FirstOrDefault(m => m.Id == GameInfoFraming.MessageId);
        if (g.Body is not { Length: >= 132 }) { Console.WriteLine("bloco ilegivel"); return; }

        uint musica = GameInfoFraming.ReadSongId(g.Body);
        var difs = library.DifficultiesFor(musica).ToList();
        var lb = difs.Count > 0
            ? library.Get(new SongKey(musica, difs[0]))?.FirstOrDefault(e => e.Id == GameInfoFraming.MessageId)
            : null;
        if (lb is not { Body.Length: >= 132 }) { Console.WriteLine("sem bloco na biblioteca"); return; }

        Console.WriteLine($"etapa {etapa}, musica {musica} — gravado vs biblioteca (mesma musica e dificuldade)");
        Console.WriteLine();
        for (int i = 0; i < 132; i += 16)
        {
            int n = Math.Min(16, 132 - i);
            string a = string.Join(" ", Enumerable.Range(i, n).Select(k => g.Body[k].ToString("x2")));
            string b = string.Join(" ", Enumerable.Range(i, n).Select(k => lb.Value.Body[k].ToString("x2")));
            string marca = string.Join("", Enumerable.Range(i, n).Select(k => g.Body[k] == lb.Value.Body[k] ? "  ." : "  X"));
            Console.WriteLine($"  +{i,-4} grav {a}");
            Console.WriteLine($"        bibl {b}");
            Console.WriteLine($"             {marca}");
        }
    }

    /// <summary>Que courses tem 0x0084 gravado, e com que chave.</summary>
    public static void CoursesGravados(params string[] recordings)
    {
        foreach (var r in recordings)
        {
            var map = ResponseMap.Load(r);
            Console.WriteLine($"--- {Path.GetFileName(r)} ---");
            foreach (var pedido in new ushort[] { 0x0083, 0x0085 })
            {
                var comRank = map.ChavesDe(pedido)
                    .Where(k => k.Respostas.Any(m => m.Id == 0x0084))
                    .OrderBy(k => k.Chave & 0xFFFF).ToList();
                Console.WriteLine($"  pedido 0x{pedido:x4}: {comRank.Count} chave(s) com 0x0084");
                foreach (var (chave, resp) in comRank)
                    Console.WriteLine($"     course {chave & 0xFFFF,-3} accao 0x{(chave >> 16) & 0xFF:x2}  " +
                                      $"({string.Join(" ", resp.Select(m => $"0x{m.Id:x2}"))})");
            }
            Console.WriteLine();
        }
    }

    private static Dictionary<uint, byte[]> Blocos(string recording)
    {
        var map = ResponseMap.Load(recording);
        var r = new Dictionary<uint, byte[]>();
        foreach (var grupo in map.AllSetsContaining(GameInfoFraming.MessageId))
            foreach (var m in grupo)
                if (m.Id == GameInfoFraming.MessageId && m.Body.Length > 8)
                    r.TryAdd(GameInfoFraming.ReadSongId(m.Body), m.Body);
        return r;
    }

    public static void Run(string recording, SongLibrary? library)
    {
        var map = ResponseMap.Load(recording);
        var grupos = map.AllSetsContaining(GameInfoFraming.MessageId);
        if (grupos.Count == 0) { Console.WriteLine("sem grupos de arranque nesta gravacao"); return; }

        Console.WriteLine($"{Path.GetFileName(recording)}: {grupos.Count} etapa(s) de arranque\n");

        for (int i = 0; i < grupos.Count; i++)
        {
            var bloco = grupos[i].FirstOrDefault(m => m.Id == GameInfoFraming.MessageId);
            if (bloco.Body is not { Length: > 8 }) { Console.WriteLine($"  {i + 1}. bloco ilegivel"); continue; }

            uint musica = GameInfoFraming.ReadSongId(bloco.Body);
            string dif = Identificar(bloco.Body, musica, library);
            // So' o PREFIXO: o resto do bloco e' comprimido e da' so' ruido.
            var nomes = Strings(bloco.Body.Take(GameInfoFraming.PrefixLength).ToArray());
            Console.WriteLine($"  {i + 1}. musica {musica,-4} {dif}   ({bloco.Body.Length} bytes)");
            if (nomes.Count > 0)
                Console.WriteLine($"       strings: {string.Join("  ", nomes.Take(8))}");
        }

        Console.WriteLine("\nNOTA: as etapas saem pela ordem da gravacao. Numa gravacao que tenha");
        Console.WriteLine("      feito um course E depois free mode, as ultimas nao sao do course.");
    }

    private static string Identificar(byte[] corpo, uint musica, SongLibrary? library)
    {
        if (library is null) return "(sem biblioteca)";

        var candidatas = library.DifficultiesFor(musica).ToList();
        if (candidatas.Count == 0) return "(a musica nao esta' na biblioteca)";

        var batem = new List<byte>();
        foreach (var d in candidatas)
        {
            var grupo = library.Get(new SongKey(musica, d));
            var chart = grupo?.FirstOrDefault(e => e.Id == GameInfoFraming.MessageId);
            if (chart is not { Body.Length: > 0 }) continue;
            if (Igual(chart.Value.Body, corpo)) batem.Add(d);
        }

        if (batem.Count == 1) return new SongKey(musica, batem[0]).DifficultyName;
        if (batem.Count == 0)
        {
            // Porque nao bateu: tamanho diferente e' uma coisa, conteudo diferente e' outra.
            var notas = new List<string>();
            foreach (var d in candidatas)
            {
                var g = library.Get(new SongKey(musica, d));
                var c = g?.FirstOrDefault(e => e.Id == GameInfoFraming.MessageId);
                if (c is not { Body.Length: > 0 }) { notas.Add($"{d}:sem bloco"); continue; }
                if (c.Value.Body.Length != corpo.Length) { notas.Add($"{d}:{c.Value.Body.Length}B"); continue; }
                int n = 0;
                for (int i = 0; i < corpo.Length; i++) if (i != ByteDois && c.Value.Body[i] != corpo[i]) n++;
                notas.Add($"{d}:difere em {n}B");
            }
            return $"(nenhuma bate — {string.Join("  ", notas)})";
        }
        return $"(ambiguo: {string.Join(", ", batem.Select(d => new SongKey(musica, d).DifficultyName))})";
    }

    /// <summary>Igualdade a menos do byte 2 — ver a nota da classe.</summary>
    private static bool Igual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (i != ByteDois && a[i] != b[i]) return false;
        return true;
    }
}
