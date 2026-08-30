namespace GrooveServer.Tools;

/// <summary>
/// Deduz a tabela de tamanhos por msgid resolvendo-a como restricao global.
///
/// Descoberta que torna isto possivel: o protocolo nao tem campo de comprimento.
/// Cada message id tem um tamanho FIXO, que o cliente conhece por tabela. Isso
/// confirmou-se empiricamente — todos os msgid observados isoladamente mais do que
/// uma vez apareceram sempre com o mesmo tamanho (ver SizeHarvester).
///
/// Logo, em vez de um comprimento livre por pacote, ha' uma unica incognita por
/// msgid, partilhada por todas as suas ocorrencias em todos os streams. Exigir que
/// cada stream se parta exatamente em pacotes cujos cabecalhos (nunca cifrados)
/// contenham msgid validos torna o sistema fortemente sobredeterminado.
/// </summary>
public static class SizeTableSolver
{
    private const int MinSize = 3;
    private const int MaxSize = 4096;

    public sealed class Result
    {
        public required Dictionary<ushort, int> Sizes { get; init; }
        public required List<List<(int Offset, ushort Id, int Len)>> Partitions { get; init; }
    }

    /// <param name="streams">Fluxos continuos de UMA direcao (um por ligacao TCP).</param>
    /// <param name="validIds">Ids aceitaveis nessa direcao.</param>
    /// <param name="seed">Tamanhos ja' conhecidos, para ancorar a busca.</param>
    /// <param name="hardBoundaries">
    /// Offsets onde um pacote TEM obrigatoriamente de comecar — tipicamente o inicio
    /// de segmentos TCP que chegaram depois de uma pausa, porque nesse caso o pacote
    /// anterior ja' tinha sido entregue por completo. Poda decisiva: limita o
    /// comprimento de cada pacote a' distancia ate' a' proxima fronteira obrigatoria.
    /// </param>
    public static Result? Solve(
        IReadOnlyList<byte[]> streams, ISet<ushort> validIds, IDictionary<ushort, int> seed,
        IReadOnlyList<SortedSet<int>>? hardBoundaries = null)
    {
        var sizes = new Dictionary<ushort, int>(seed);
        long nodes = 0;

        // proxima fronteira obrigatoria estritamente a' frente de off
        int NextHard(int si, int off)
        {
            if (hardBoundaries is null || si >= hardBoundaries.Count) return int.MaxValue;
            foreach (var b in hardBoundaries[si]) if (b > off) return b;
            return int.MaxValue;
        }

        bool Search(int si, int off)
        {
            if (++nodes > 50_000_000) return false;      // travao de seguranca

            while (si < streams.Count && off == streams[si].Length) { si++; off = 0; }
            if (si >= streams.Count) return true;

            var s = streams[si];
            if (off + 2 > s.Length) return false;

            ushort id = BitConverter.ToUInt16(s, off);
            if (!validIds.Contains(id)) return false;

            int limit = NextHard(si, off);
            int maxLen = limit == int.MaxValue ? MaxSize : Math.Min(MaxSize, limit - off);

            if (sizes.TryGetValue(id, out int known))
                return known <= maxLen && off + known <= s.Length && Search(si, off + known);

            for (int len = MinSize; len <= maxLen; len++)
            {
                if (off + len > s.Length) break;
                // poda barata: a fronteira seguinte tem de ser um msgid valido
                int next = off + len;
                if (next < s.Length &&
                    (next + 2 > s.Length || !validIds.Contains(BitConverter.ToUInt16(s, next))))
                    continue;

                sizes[id] = len;
                if (Search(si, next)) return true;
                sizes.Remove(id);
            }
            return false;
        }

        if (!Search(0, 0)) return null;

        // reconstruir as particoes com a tabela resolvida
        var parts = new List<List<(int, ushort, int)>>();
        foreach (var s in streams)
        {
            var list = new List<(int, ushort, int)>();
            int off = 0;
            while (off < s.Length)
            {
                ushort id = BitConverter.ToUInt16(s, off);
                int len = sizes[id];
                list.Add((off, id, len));
                off += len;
            }
            parts.Add(list);
        }
        return new Result { Sizes = sizes, Partitions = parts };
    }

    public static void Run(string direction, params string[] paths)
    {
        // Cada ficheiro e' uma ligacao; juntar os segmentos da direcao pedida e
        // registar onde um pacote tem obrigatoriamente de comecar.
        const double GapSeconds = 0.050;
        var streams = new List<byte[]>();
        var hard = new List<SortedSet<int>>();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var segs = File.ReadAllLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Split('\t'))
                .Where(p => p.Length >= 3 && p[0] == direction && p[2].Length > 0)
                .Select(p => (Time: double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                              Data: Convert.FromHexString(p[2])))
                .ToList();
            if (segs.Count == 0) continue;

            var bytes = segs.SelectMany(s => s.Data).ToArray();
            var bounds = new SortedSet<int> { 0 };
            int cursor = 0;
            for (int i = 0; i < segs.Count; i++)
            {
                // pausa desde o segmento anterior => o que veio antes ja' fechou
                if (i > 0 && segs[i].Time - segs[i - 1].Time >= GapSeconds) bounds.Add(cursor);
                cursor += segs[i].Data.Length;
            }
            streams.Add(bytes);
            hard.Add(bounds);
            Console.WriteLine($"{path}: {bytes.Length} bytes, {bounds.Count} fronteiras obrigatorias");
        }

        // Ancoras vindas dos segmentos isolados (SizeHarvester) — certezas.
        var seed = direction == "S2C"
            ? new Dictionary<ushort, int> { [0x03] = 3, [0x07] = 3, [0x0A] = 47, [0x3A] = 51, [0x65] = 11, [0x71] = 15, [0xA7] = 12 }
            : new Dictionary<ushort, int> { [0x04] = 3, [0x06] = 3, [0x0A] = 23, [0x1B] = 53, [0x6C] = 30, [0x72] = 259 };

        var ids = direction == "S2C"
            ? FramingSolver.ServerToClientIds
            : new HashSet<ushort>(Enumerable.Range(1, 0x140).Select(i => (ushort)i));

        Console.WriteLine($"\nA resolver {direction} com {seed.Count} ancoras...");
        var r = Solve(streams, ids, seed, hard);
        if (r is null) { Console.WriteLine("Sem solucao consistente."); return; }

        Console.WriteLine($"\n=== TABELA DE TAMANHOS ({r.Sizes.Count} msgid) ===");
        foreach (var (id, len) in r.Sizes.OrderBy(k => k.Key))
        {
            string name = SizeHarvester.MessageNames.GetValueOrDefault(id, "");
            string anchored = seed.ContainsKey(id) ? " (ancora)" : "";
            Console.WriteLine($"  0x{id:x4} ({id,3}) = {len,5}  {name}{anchored}");
        }

        Console.WriteLine("\n=== PARTICOES ===");
        for (int i = 0; i < r.Partitions.Count; i++)
        {
            Console.WriteLine($"-- stream {i}: {r.Partitions[i].Count} pacotes");
            foreach (var (off, id, len) in r.Partitions[i].Take(40))
                Console.WriteLine($"     0x{off:x4} len={len,5} 0x{id:x4} {SizeHarvester.MessageNames.GetValueOrDefault(id, "")}");
        }
    }
}
