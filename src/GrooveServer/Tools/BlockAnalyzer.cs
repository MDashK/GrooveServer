using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Analisa o bloco opaco do GameInfoInf.
///
/// Nao e' zlib nem deflate, mas tambem nao parece comprimido: ha' padroes visiveis que
/// se repetem (`f4 57 f4 f4 72`), e compressao destroi padroes. Isso aponta para
/// obfuscacao simples — tipicamente XOR com uma chave repetida.
///
/// Se for esse o caso, e se os dados por baixo forem sobretudo zeros (normal em
/// registos com campos reservados), entao o byte mais frequente em cada posicao modulo
/// o comprimento da chave E' a chave nessa posicao. Testam-se varios comprimentos e
/// escolhe-se aquele cuja "chave" explica melhor os dados.
/// </summary>
public static class BlockAnalyzer
{
    public static void Run(string path, ushort msgId = 0x007A)
    {
        var body = ExtractBody(path, msgId);
        if (body is null) { Console.WriteLine($"mensagem 0x{msgId:x4} nao encontrada"); return; }

        Console.WriteLine($"corpo de 0x{msgId:x4}: {body.Length} bytes");
        int declared = body.Length >= 132 ? BitConverter.ToInt32(body, 128) : 0;
        Console.WriteLine($"comprimento declarado em +128: {declared}   (restam {body.Length - 132})");

        var block = body.AsSpan(132).ToArray();
        Console.WriteLine($"bloco: {block.Length} bytes, entropia {Entropy(block):F2}/8.00\n");

        // Distribuicao global: se fosse comprimido seria quase plana.
        var freq = new int[256];
        foreach (var b in block) freq[b]++;
        var top = Enumerable.Range(0, 256).OrderByDescending(i => freq[i]).Take(8);
        Console.WriteLine("bytes mais frequentes: " +
            string.Join("  ", top.Select(i => $"{i:x2}={freq[i] * 100.0 / block.Length:F1}%")));

        Console.WriteLine("\ncomprimento de chave candidato (quanto maior a fracao, melhor):");
        int bestLen = 0; double bestScore = 0;
        for (int len = 1; len <= 64; len++)
        {
            double sum = 0;
            for (int p = 0; p < len; p++)
            {
                var counts = new int[256];
                int n = 0;
                for (int i = p; i < block.Length; i += len) { counts[block[i]]++; n++; }
                sum += counts.Max() / (double)n;
            }
            double score = sum / len;
            if (score > bestScore) { bestScore = score; bestLen = len; }
            if (score > 0.10) Console.WriteLine($"  {len,3} bytes: {score:P1}");
        }
        Console.WriteLine($"\nmelhor: {bestLen} bytes ({bestScore:P1})");

        if (bestScore < 0.12)
        {
            Console.WriteLine("Fraco — nao parece XOR de chave repetida sobre dados esparsos.");
            return;
        }

        // Reconstruir a chave assumindo que o byte mais frequente corresponde a zero
        var key = new byte[bestLen];
        for (int p = 0; p < bestLen; p++)
        {
            var counts = new int[256];
            for (int i = p; i < block.Length; i += bestLen) counts[block[i]]++;
            key[p] = (byte)Array.IndexOf(counts, counts.Max());
        }
        Console.WriteLine($"chave deduzida: {Convert.ToHexString(key)}");

        var plain = (byte[])block.Clone();
        for (int i = 0; i < plain.Length; i++) plain[i] ^= key[i % bestLen];

        Console.WriteLine($"apos XOR: entropia {Entropy(plain):F2}/8.00");
        Console.WriteLine("primeiros 160 bytes:");
        for (int i = 0; i < Math.Min(160, plain.Length); i += 16)
        {
            var c = plain.AsSpan(i, Math.Min(16, plain.Length - i)).ToArray();
            Console.WriteLine($"  +{i,5}  {string.Join(' ', c.Select(b => b.ToString("x2"))),-47}  " +
                new string(c.Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray()));
        }
    }

    private static double Entropy(byte[] d)
    {
        var f = new int[256];
        foreach (var b in d) f[b]++;
        double h = 0;
        foreach (var c in f) if (c > 0) { double p = (double)c / d.Length; h -= p * Math.Log2(p); }
        return h;
    }

    private static byte[]? ExtractBody(string path, ushort msgId)
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

        int pos = 3 + 47;
        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);
            int? size = MessageSizes.FromServer(id, stream, pos);
            if (size is null || pos + size.Value > stream.Length) break;
            int len = size.Value;
            byte[] body = Array.Empty<byte>();
            if (len >= PacketCodec.MinEncryptedSize)
            {
                body = stream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
            }
            if (id == msgId) return body;
            pos += len;
        }
        return null;
    }
}
