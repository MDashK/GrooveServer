using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Compara varias ocorrencias da mesma mensagem do servidor numa captura.
///
/// Foi assim que se descobriu o id da musica: gravando escolhas diferentes e vendo que
/// bytes mudam. Aqui aplica-se o mesmo ao <c>GameInfoInf</c>, cujo bloco de 18 KB
/// resistiu a zlib, deflate e XOR de chave repetida. Se so' mudarem alguns bytes entre
/// musicas, esses sao os dados por musica e o resto e' estrutura fixa; se mudar quase
/// tudo, o bloco e' gerado de raiz e nao vale a pena tentar remenda-lo.
/// </summary>
public static class BlockCompare
{
    public static void Run(string path, ushort msgId = 0x007A)
    {
        var bodies = ExtractAll(path, msgId);
        Console.WriteLine($"{bodies.Count} ocorrencia(s) de 0x{msgId:x4}\n");
        if (bodies.Count < 2) { Console.WriteLine("preciso de pelo menos duas para comparar"); return; }

        int len = bodies.Min(b => b.Length);
        Console.WriteLine("tamanhos: " + string.Join(", ", bodies.Select(b => b.Length)));

        int differing = 0;
        var runs = new List<(int Start, int Length)>();
        int runStart = -1;
        for (int i = 0; i < len; i++)
        {
            bool diff = bodies.Any(b => b[i] != bodies[0][i]);
            if (diff)
            {
                differing++;
                if (runStart < 0) runStart = i;
            }
            else if (runStart >= 0) { runs.Add((runStart, i - runStart)); runStart = -1; }
        }
        if (runStart >= 0) runs.Add((runStart, len - runStart));

        Console.WriteLine($"bytes diferentes: {differing} de {len} ({differing * 100.0 / len:F1}%)");
        Console.WriteLine($"zonas contiguas que mudam: {runs.Count}\n");

        foreach (var (start, length) in runs.Take(40))
        {
            Console.WriteLine($"  +{start,-6} .. +{start + length - 1,-6}  ({length} bytes)");
            if (length <= 8)
                foreach (var b in bodies)
                    Console.WriteLine($"      {Convert.ToHexString(b.AsSpan(start, length).ToArray())}");
        }
        if (runs.Count > 40) Console.WriteLine($"  ... e mais {runs.Count - 40} zonas");

        Console.WriteLine("\n=== zonas IGUAIS em todas (estrutura fixa) ===");
        int prev = 0; int shown = 0;
        foreach (var (start, length) in runs)
        {
            if (start > prev && shown++ < 20)
                Console.WriteLine($"  +{prev,-6} .. +{start - 1,-6}  ({start - prev} bytes iguais)");
            prev = start + length;
        }
        if (prev < len) Console.WriteLine($"  +{prev,-6} .. +{len - 1,-6}  ({len - prev} bytes iguais)");
    }

    private static List<byte[]> ExtractAll(string path, ushort msgId)
    {
        var stream = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[0] == "S2C" && p[2].Length > 0)
            .SelectMany(p => Convert.FromHexString(p[2]))
            .ToArray();

        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        var found = new List<byte[]>();
        int pos = 3 + 47;
        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);

            // O GameInfoInf tem tamanho variavel e o comprimento vem cifrado no proprio
            // corpo; e' preciso decifrar o prefixo para saber onde o pacote acaba.
            if (id == GameInfoFraming.MessageId)
            {
                int total;
                byte[] prefix;
                try { total = GameInfoFraming.ReadTotalLength(stream, pos, cipher, out prefix); }
                catch (Exception ex) { Console.WriteLine($"  (GameInfoInf em 0x{pos:x4}: {ex.Message})"); break; }

                int rest = total - PacketCodec.HeaderSize - GameInfoFraming.PrefixLength;
                if (pos + total > stream.Length) { Console.WriteLine($"  (GameInfoInf truncado em 0x{pos:x4})"); break; }

                var tail = stream.AsSpan(pos + PacketCodec.HeaderSize + GameInfoFraming.PrefixLength, rest).ToArray();
                cipher.Decrypt(tail);

                var full = new byte[prefix.Length + tail.Length];
                prefix.CopyTo(full, 0);
                tail.CopyTo(full, prefix.Length);
                if (id == msgId)
                {
                    Console.WriteLine($"  0x{pos:x4}: GameInfoInf musica {GameInfoFraming.ReadSongId(full)}, " +
                                      $"total {total} bytes (bloco {rest})");
                    found.Add(full);
                }
                pos += total;
                continue;
            }

            int? size = MessageSizes.FromServer(id, stream, pos);
            if (size is null || pos + size.Value > stream.Length)
            { Console.WriteLine($"  (paragem em 0x{pos:x4}, msgid 0x{id:x4})"); break; }
            int len = size.Value;
            if (len >= PacketCodec.MinEncryptedSize)
            {
                var body = stream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
                if (id == msgId) found.Add(body);
            }
            pos += len;
        }
        return found;
    }
}
