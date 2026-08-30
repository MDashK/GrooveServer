using System.Globalization;
using System.Text;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Resolve os tamanhos servidor -&gt; cliente decompondo as rajadas com tres
/// restricoes combinadas:
///
///  1. cada fronteira tem de cair num message id valido (cabecalhos nunca cifrados);
///  2. cada tamanho tem de respeitar o minimo lido do handler correspondente
///     (o maior offset que o handler acede no pacote — ver RecvBounds);
///  3. o resultado tem de decifrar para algo plausivel, e o stream tem de conter
///     o nome do jogador — o cliente mostra-o no ecra, logo veio do servidor.
///
/// A terceira e' o oraculo decisivo: uma decomposicao errada dessincroniza a cifra
/// e nunca produz a string.
/// </summary>
public static class BurstSolver
{
    /// <summary>Minimos vindos dos handlers (RecvBounds). 0 = sem informacao.</summary>
    public static readonly Dictionary<ushort, int> LowerBounds = new()
    {
        [0x03] = 3, [0x07] = 3, [0x08] = 7, [0x0A] = 47, [0x0B] = 11, [0x0C] = 11,
        [0x10] = 92, [0x12] = 11, [0x14] = 3, [0x16] = 3, [0x18] = 7, [0x1A] = 49,
        [0x1E] = 3, [0x1F] = 64, [0x20] = 74, [0x22] = 74, [0x24] = 13, [0x25] = 73,
        [0x26] = 17, [0x27] = 13, [0x28] = 21, [0x29] = 49, [0x2A] = 199, [0x2B] = 135,
        [0x2C] = 247, [0x2D] = 71, [0x2E] = 127, [0x2F] = 11, [0x31] = 3, [0x33] = 3,
        [0x35] = 3, [0x37] = 12, [0x39] = 12, [0x3A] = 51, [0x3B] = 7, [0x3C] = 62,
        [0x3D] = 7, [0x40] = 3, [0x41] = 67, [0x42] = 3, [0x43] = 7, [0x44] = 7,
        [0x45] = 7, [0x47] = 10, [0x48] = 3, [0x4D] = 7, [0x50] = 45, [0x51] = 67,
        [0x54] = 8, [0x55] = 3, [0x59] = 8, [0x5B] = 7, [0x5E] = 10, [0x60] = 8,
        [0x61] = 7, [0x63] = 3, [0x65] = 11, [0x68] = 3, [0x6B] = 3, [0x6D] = 8,
        [0x70] = 8, [0x71] = 15, [0x74] = 7, [0x77] = 11, [0x78] = 21, [0x7A] = 139,
        [0x7C] = 7,
    };

    /// <summary>Tamanhos que consideramos certos (segmentos isolados + minimo coincidente).</summary>
    public static readonly Dictionary<ushort, int> Known =
        new(Protocol.MessageSizes.ServerToClient);

    public static void Run(string path, string playerName)
    {
        const double GapSeconds = 0.030;
        var segs = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == "S2C" && p[2].Length > 0)
            .Select(p => (Time: double.Parse(p[1], CultureInfo.InvariantCulture),
                          Data: Convert.FromHexString(p[2])))
            .ToList();
        if (segs.Count == 0) { Console.WriteLine("sem segmentos S2C"); return; }

        var stream = segs.SelectMany(s => s.Data).ToArray();
        var hard = new SortedSet<int> { 0, stream.Length };
        int cur = 0;
        for (int i = 0; i < segs.Count; i++)
        {
            if (i > 0 && segs[i].Time - segs[i - 1].Time >= GapSeconds) hard.Add(cur);
            cur += segs[i].Data.Length;
        }
        Console.WriteLine($"{stream.Length} bytes, {hard.Count} fronteiras obrigatorias");

        // cifra armada pelo ConnectAck (hello 3 + ConnectAck 47)
        const int anchor = 3 + 47;
        if (stream.Length < anchor || BitConverter.ToUInt16(stream, 3) != 0x000A)
        { Console.WriteLine("stream nao comeca com hello + ConnectAck"); return; }
        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher0 = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        var sizes = new Dictionary<ushort, int>(Known);
        var name = Encoding.ASCII.GetBytes(playerName);
        var plain = new List<byte>();
        long nodes = 0;

        int NextHard(int off) { foreach (var b in hard) if (b > off) return b; return stream.Length; }

        bool Search(int off, DjMaxCipher state, List<(int, ushort, int)> acc)
        {
            if (++nodes > 5_000_000) return false;
            if (off == stream.Length) return true;
            if (off + 2 > stream.Length) return false;

            ushort id = BitConverter.ToUInt16(stream, off);
            if (!FramingSolver.ServerToClientIds.Contains(id)) return false;

            int limit = Math.Min(NextHard(off), stream.Length);
            int lo = Math.Max(3, LowerBounds.GetValueOrDefault(id, 3));
            var candidates = sizes.TryGetValue(id, out int fixedLen)
                ? new[] { fixedLen }
                : Enumerable.Range(lo, Math.Max(0, limit - off - lo + 1)).ToArray();

            foreach (var len in candidates)
            {
                int next = off + len;
                if (next > limit) continue;
                if (next < stream.Length &&
                    (next + 2 > stream.Length ||
                     !FramingSolver.ServerToClientIds.Contains(BitConverter.ToUInt16(stream, next))))
                    continue;

                var probe = state.Clone();
                byte[] body = Array.Empty<byte>();
                if (len >= PacketCodec.MinEncryptedSize)
                {
                    body = stream.AsSpan(off + 7, len - 7).ToArray();
                    probe.Decrypt(body);
                    int filler = body.Count(b => b is 0x00 or 0xCC);
                    if (body.Length >= 16 && (double)filler / body.Length < 0.10) continue;
                }

                bool added = !sizes.ContainsKey(id);
                if (added) sizes[id] = len;
                acc.Add((off, id, len));
                plain.AddRange(body);

                if (Search(next, probe, acc)) return true;

                plain.RemoveRange(plain.Count - body.Length, body.Length);
                acc.RemoveAt(acc.Count - 1);
                if (added) sizes.Remove(id);
            }
            return false;
        }

        var packets = new List<(int, ushort, int)> { (0, 0x0003, 3), (3, 0x000A, 47) };
        if (!Search(anchor, cipher0, packets))
        { Console.WriteLine($"sem decomposicao consistente ({nodes} nos explorados)"); return; }

        bool hasName = Contains(plain.ToArray(), name);
        Console.WriteLine($"\nDecomposicao encontrada: {packets.Count} pacotes, {nodes} nos");
        Console.WriteLine($"Nome do jogador no texto decifrado: {(hasName ? "SIM" : "NAO — decomposicao suspeita")}\n");

        foreach (var (off, id, len) in packets)
            Console.WriteLine($"  0x{off:x4} len={len,5} 0x{id:x4} {SizeHarvester.MessageNames.GetValueOrDefault(id, "")}");

        Console.WriteLine("\n=== TABELA S2C ===");
        foreach (var (id, len) in sizes.OrderBy(k => k.Key))
            Console.WriteLine($"  0x{id:x4} ({id,3}) = {len,5}  {SizeHarvester.MessageNames.GetValueOrDefault(id, "")}");
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++) if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return true;
        }
        return false;
    }
}
