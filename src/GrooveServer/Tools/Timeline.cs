using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Imprime a troca de mensagens pela ordem em que aconteceu, nos dois sentidos.
///
/// O <c>ResponseMap</c> agrupa respostas por pedido, o que e' util para servir mas
/// esconde a sequencia. Para perceber um fluxo â€” o fim de uma musica, por exemplo â€” e'
/// preciso ver o que veio a seguir a que', na ordem real.
/// </summary>
public static class Timeline
{
    /// <summary>Quantas ocorrencias saltar antes de mostrar.</summary>
    public static int Skip { get; set; }

    /// <summary>Quantos bytes do corpo mostrar em cada linha.</summary>
    public static int BodyBytes { get; set; } = 16;

    public static void Run(string path, string aroundHex = "", int context = 12)
    {
        ushort around = string.IsNullOrEmpty(aroundHex) ? (ushort)0 : Convert.ToUInt16(aroundHex, 16);

        var events = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[2].Length > 0)
            .Select(p => (Dir: p[0],
                          Time: double.Parse(p[1], CultureInfo.InvariantCulture),
                          Data: Convert.FromHexString(p[2])))
            .OrderBy(e => e.Time)
            .ToList();

        var serverBytes = events.Where(e => e.Dir == "S2C").SelectMany(e => e.Data).ToArray();
        var clientBytes = events.Where(e => e.Dir == "C2S").SelectMany(e => e.Data).ToArray();

        var key = PacketCodec.TransformSessionKey(serverBytes.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var sCipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);
        var cCipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        // percorrer cada direcao produzindo (posicao no fluxo, id, corpo decifrado)
        var server = Walk(serverBytes, sCipher, fromServer: true).ToList();
        var client = Walk(clientBytes, cCipher, fromServer: false).ToList();

        // intercalar usando os tempos dos segmentos: para cada segmento, quantos bytes
        // dessa direcao ja' tinham passado
        var line = new List<(double Time, bool FromServer, int Offset)>();
        int sAcc = 0, cAcc = 0;
        foreach (var e in events)
        {
            if (e.Dir == "S2C") { line.Add((e.Time, true, sAcc)); sAcc += e.Data.Length; }
            else { line.Add((e.Time, false, cAcc)); cAcc += e.Data.Length; }
        }

        var merged = new List<(double Time, bool FromServer, ushort Id, byte[] Body, byte[] Head)>();
        foreach (var (isServer, list) in new[] { (true, server), (false, client) })
            foreach (var (offset, id, body, head) in list)
            {
                double time = line.Where(l => l.FromServer == isServer && l.Offset <= offset)
                                  .Select(l => l.Time).DefaultIfEmpty(0).Max();
                merged.Add((time, isServer, id, body, head));
            }
        merged = merged.OrderBy(m => m.Time).ToList();

        // localizar os pontos de interesse
        var marks = around == 0
            ? Enumerable.Range(0, merged.Count).ToList()
            : merged.Select((m, i) => (m, i)).Where(x => x.m.Id == around).Select(x => x.i).ToList();

        Console.WriteLine($"{merged.Count} mensagens; {marks.Count} ocorrencia(s) de 0x{around:x4}\n");

        var shown = new HashSet<int>();

        // Sem mensagem-alvo, imprime a sequencia toda de uma vez — e' o que serve para
        // comparar duas sessoes lado a lado.
        if (around == 0)
        {
            foreach (var m in merged)
                Console.WriteLine($"{(m.FromServer ? "S" : "C")} 0x{m.Id:x4} " +
                                  SizeHarvester.MessageNames.GetValueOrDefault(m.Id, "") +
                                  (BodyBytes > 0 && m.Body.Length > 0
                                      ? " " + Convert.ToHexString(
                                            m.Body.AsSpan(0, Math.Min(BodyBytes, m.Body.Length)).ToArray())
                                      : ""));
            return;
        }

        foreach (var mark in marks.Skip(Skip).Take(4))
        {
            Console.WriteLine($"---- contexto de 0x{around:x4} (#{mark}) ----");
            for (int i = Math.Max(0, mark - context); i < Math.Min(merged.Count, mark + context); i++)
            {
                if (!shown.Add(i)) continue;
                var m = merged[i];
                string arrow = m.FromServer ? "  <-" : "->  ";
                string name = SizeHarvester.MessageNames.GetValueOrDefault(m.Id, "");
                string mark2 = i == mark ? "  ***" : "";
                Console.WriteLine($"  {m.Time,8:F2}s {arrow} 0x{m.Id:x4} {name,-22} cab={Convert.ToHexString(m.Head)} " +
                                  $"{(m.Body.Length > 0 ? Convert.ToHexString(m.Body.AsSpan(0, Math.Min(BodyBytes, m.Body.Length)).ToArray()) : "")}{mark2}");
            }
            Console.WriteLine();
        }
    }

    private static IEnumerable<(int Offset, ushort Id, byte[] Body, byte[] Head)> Walk(
        byte[] stream, DjMaxCipher cipher, bool fromServer)
    {
        int pos = fromServer ? 0 : 0;
        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);
            int len;
            if (fromServer && id == GameInfoFraming.MessageId)
            {
                int total;
                try { total = GameInfoFraming.ReadTotalLength(stream, pos, cipher, out _); } catch { yield break; }
                if (pos + total > stream.Length) yield break;
                int rest = total - PacketCodec.HeaderSize - GameInfoFraming.PrefixLength;
                var tail = stream.AsSpan(pos + PacketCodec.HeaderSize + GameInfoFraming.PrefixLength, rest).ToArray();
                cipher.Decrypt(tail);
                yield return (pos, id, Array.Empty<byte>(), stream.AsSpan(pos, 7).ToArray());
                pos += total;
                continue;
            }

            int? size = fromServer ? MessageSizes.FromServer(id, stream, pos) : MessageSizes.FromClient(id);
            if (size is null || pos + size.Value > stream.Length) yield break;
            len = size.Value;

            byte[] body = Array.Empty<byte>();
            if (len >= PacketCodec.MinEncryptedSize && !(fromServer && (id == 0x0003 || id == 0x000A)) && !(!fromServer && id == 0x000A))
            {
                body = stream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
            }
            yield return (pos, id, body, stream.AsSpan(pos, Math.Min(7, len)).ToArray());
            pos += len;
        }
    }
}



