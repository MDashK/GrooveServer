using System.Globalization;
using System.Net.Sockets;
using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Verifica que o servidor serve o bloco certo quando a musica escolhida NAO e' a que
/// estava no template daquela ocorrencia.
///
/// E' o caso que rebentou o servidor: o template da primeira ocorrencia era o bloco da
/// musica 0 (12986 bytes) e o jogador escolheu a 1 (18324). Dimensionar o pacote pelo
/// template estourava o vetor.
///
/// O id da musica vai em claro no cabecalho do ChangeDiscReq (bytes 3..6), por isso da'
/// para forjar qualquer escolha sem mexer na parte cifrada.
/// </summary>
public static class SongSwitchTest
{
    public static async Task RunAsync(string capturePath, uint songId, string host, int port)
    {
        var events = File.ReadAllLines(capturePath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[2].Length > 0)
            .Select(p => (Dir: p[0],
                          Time: double.Parse(p[1], CultureInfo.InvariantCulture),
                          Data: Convert.FromHexString(p[2])))
            .OrderBy(e => e.Time)
            .ToList();

        var clientStream = events.Where(e => e.Dir == "C2S").SelectMany(e => e.Data).ToArray();

        // partir em pacotes e parar depois do primeiro StartReq
        var packets = new List<byte[]>();
        int off = 0;
        while (off + 2 <= clientStream.Length)
        {
            ushort id = BitConverter.ToUInt16(clientStream, off);
            int? size = MessageSizes.FromClient(id);
            if (size is null || off + size.Value > clientStream.Length) break;
            var pkt = clientStream.AsSpan(off, size.Value).ToArray();

            if (id == 0x0076)      // ChangeDiscReq: forjar a musica escolhida
            {
                uint original = BitConverter.ToUInt32(pkt, 3);
                BitConverter.TryWriteBytes(pkt.AsSpan(3, 4), songId);
                Console.WriteLine($"  ChangeDiscReq: musica {original} -> {songId} (forjado)");
            }
            packets.Add(pkt);
            off += size.Value;
            if (id == 0x005F) break;   // StartReq: e' aqui que o GameInfoInf sai
        }

        Console.WriteLine($"a enviar {packets.Count} pacotes ate' ao StartReq\n");

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        var ns = tcp.GetStream();
        var buf = new byte[64 * 1024];
        var received = new List<byte>();

        async Task DrainAsync(int quietMs)
        {
            var idle = DateTime.UtcNow.AddMilliseconds(quietMs);
            while (DateTime.UtcNow < idle)
            {
                if (ns.DataAvailable)
                {
                    int n = await ns.ReadAsync(buf);
                    if (n == 0) return;
                    received.AddRange(buf.AsSpan(0, n).ToArray());
                    idle = DateTime.UtcNow.AddMilliseconds(quietMs);
                }
                else await Task.Delay(10);
            }
        }

        await DrainAsync(400);
        foreach (var pkt in packets)
        {
            await ns.WriteAsync(pkt);
            await DrainAsync(300);
        }
        await DrainAsync(800);

        Console.WriteLine($"recebidos {received.Count} bytes do servidor");

        // localizar o GameInfoInf na resposta e conferir o tamanho e o id
        var data = received.ToArray();
        int pos = 3 + 47;
        var key = PacketCodec.TransformSessionKey(data.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new Crypto.DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        while (pos + 2 <= data.Length)
        {
            ushort id = BitConverter.ToUInt16(data, pos);
            if (id == GameInfoFraming.MessageId)
            {
                int total = GameInfoFraming.ReadTotalLength(data, pos, cipher, out var prefix);
                uint served = GameInfoFraming.ReadSongId(prefix);
                Console.WriteLine($"\nGameInfoInf recebido: {total} bytes, musica {served}");
                Console.WriteLine(served == songId
                    ? "  OK — o servidor serviu a musica pedida."
                    : $"  FALHA — pedida {songId}, servida {served}.");
                Console.WriteLine(pos + total <= data.Length
                    ? "  OK — o pacote esta' completo no que foi recebido."
                    : $"  FALHA — pacote declara {total} mas so' chegaram {data.Length - pos}.");
                return;
            }
            int? size = MessageSizes.FromServer(id, data, pos);
            if (size is null || pos + size.Value > data.Length) break;
            if (size.Value >= PacketCodec.MinEncryptedSize)
            {
                var body = data.AsSpan(pos + 7, size.Value - 7).ToArray();
                cipher.Decrypt(body);
            }
            pos += size.Value;
        }
        Console.WriteLine("\nFALHA — nao apareceu nenhum GameInfoInf na resposta.");
    }
}
