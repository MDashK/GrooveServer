using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Reproduz uma sessao capturada e decifra-a por completo.
///
/// As cifras sao continuas por direcao, portanto e' preciso replicar os pacotes
/// pela ordem exata em que passaram na rede. Pacotes com menos de 8 bytes nao sao
/// cifrados e nao fazem avancar o estado.
///
/// Formato do ficheiro de entrada: uma linha por pacote,
///   DIR &lt;TAB&gt; tempo &lt;TAB&gt; hex
/// com DIR em { C2S, S2C }.
/// </summary>
public static class StreamAnalyzer
{
    public static void Run(string path)
    {
        var packets = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3)
            .Select(p => (Dir: p[0], Time: p[1], Bytes: Convert.FromHexString(p[2])))
            .ToList();

        Console.WriteLine($"{packets.Count} pacotes de {path}\n");

        DjMaxCipher? c2s = null, s2c = null;

        foreach (var (dir, time, raw) in packets)
        {
            var pkt = (byte[])raw.Clone();
            ushort msgId = BitConverter.ToUInt16(pkt, 0);
            bool encrypted = pkt.Length >= PacketCodec.MinEncryptedSize;

            if (encrypted)
            {
                var cipher = dir == "C2S" ? c2s : s2c;
                cipher?.Decrypt(pkt.AsSpan(PacketCodec.HeaderSize));
            }

            Console.WriteLine($"--- {dir} t={time} len={pkt.Length} msgid=0x{msgId:x4} ({msgId}) " +
                              $"keybyte=0x{pkt[2]:x2} hdr={Convert.ToHexString(pkt.AsSpan(3, Math.Min(4, pkt.Length - 3)).ToArray())}" +
                              $"{(encrypted && (dir == "C2S" ? c2s : s2c) is null ? "  [EM CLARO — cifra ainda nao existe]" : "")}");

            if (pkt.Length > PacketCodec.HeaderSize)
            {
                var body = pkt.AsSpan(PacketCodec.HeaderSize).ToArray();
                Console.WriteLine("    " + Ascii(body));
            }

            // O ConnectAck entrega a chave: inicializar AMBAS as cifras logo a seguir.
            if (msgId == (ushort)MessageId.Connect && dir == "S2C" && pkt.Length >= 39 && s2c is null)
            {
                var key = PacketCodec.TransformSessionKey(raw.AsSpan(7, 32));
                var words = new uint[8];
                for (int i = 0; i < 8; i++) words[i] = BitConverter.ToUInt32(key, i * 4);
                uint chain0 = BitConverter.ToUInt16(key, 0x1C);
                c2s = new DjMaxCipher(chain0, 0u, words);
                s2c = new DjMaxCipher(chain0, 0u, words);
                Console.WriteLine($"    >>> chave de sessao {Convert.ToHexString(key)} — cifras armadas");
            }
        }
    }

    /// <summary>Vista ASCII com os bytes nao imprimiveis como '.', em linhas de 64.</summary>
    private static string Ascii(byte[] data)
    {
        var text = new string(data.Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray());
        var lines = new List<string>();
        for (int i = 0; i < text.Length; i += 64)
            lines.Add(text.Substring(i, Math.Min(64, text.Length - i)));
        return string.Join("\n    ", lines);
    }
}
