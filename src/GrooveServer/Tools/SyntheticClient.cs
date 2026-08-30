using System.Globalization;
using System.Net.Sockets;
using GrooveServer.Net;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Cliente de teste que reproduz os pacotes reais do jogo contra o nosso servidor e
/// compara as respostas com as que o servidor original deu.
///
/// Serve para validar o servidor sem depender do jogo — que, para ja', nao ha' como
/// redirecionar (o endereco esta' dentro de um .pak comprimido). Se as respostas
/// baterem byte a byte, o servidor esta' a conduzir a sessao corretamente.
/// </summary>
public static class SyntheticClient
{
    public static async Task RunAsync(string capturePath, string host, int port)
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

        // pacotes do cliente, delimitados pela tabela (um segmento pode ter varios)
        var clientStream = events.Where(e => e.Dir == "C2S").SelectMany(e => e.Data).ToArray();
        var clientPackets = Split(clientStream, MessageSizes.ClientToServer, "C2S");
        var serverStream = events.Where(e => e.Dir == "S2C").SelectMany(e => e.Data).ToArray();

        Console.WriteLine($"guiao: {clientPackets.Count} pacotes do cliente, " +
                          $"{serverStream.Length} bytes do servidor original");

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        Console.WriteLine($"ligado a {host}:{port}\n");
        var ns = tcp.GetStream();

        var received = new List<byte>();
        var buf = new byte[64 * 1024];
        int matched = 0, diverged = -1;

        // Nao usar CancellationToken num ReadAsync de socket: cancelar um read em curso
        // fecha a ligacao em .NET. Sondar DataAvailable e' o que permite "ler o que
        // houver e parar quando ficar quieto" sem estragar o socket.
        async Task DrainAsync(int quietMs)
        {
            var idleUntil = DateTime.UtcNow.AddMilliseconds(quietMs);
            while (DateTime.UtcNow < idleUntil)
            {
                if (ns.DataAvailable)
                {
                    int n = await ns.ReadAsync(buf);
                    if (n == 0) return;
                    received.AddRange(buf.AsSpan(0, n).ToArray());
                    idleUntil = DateTime.UtcNow.AddMilliseconds(quietMs);
                }
                else await Task.Delay(10);
            }
        }

        await DrainAsync(500);
        foreach (var (pkt, i) in clientPackets.Select((p, i) => (p, i)))
        {
            await ns.WriteAsync(pkt);
            await DrainAsync(300);

            // comparar o que recebemos ate' agora com o guiao original
            int cmp = Math.Min(received.Count, serverStream.Length);
            int k = 0;
            while (k < cmp && received[k] == serverStream[k]) k++;
            matched = k;
            if (k < cmp && diverged < 0) diverged = k;

            ushort id = BitConverter.ToUInt16(pkt, 0);
            Console.WriteLine($"  [{i,3}] -> 0x{id:x4} {pkt.Length,4}B   recebidos {received.Count,6}B   " +
                              $"iguais ao original: {matched,6}B" +
                              (diverged >= 0 ? $"   DIVERGE em 0x{diverged:x}" : ""));
            if (diverged >= 0) break;
        }

        Console.WriteLine();
        if (diverged < 0)
            Console.WriteLine($"RESULTADO: {matched} bytes identicos ao servidor original, sem divergencia.");
        else
        {
            Console.WriteLine($"RESULTADO: divergiu no offset 0x{diverged:x} ({diverged}).");
            int a = Math.Max(0, diverged - 8);
            Console.WriteLine($"  esperado: {Convert.ToHexString(serverStream.AsSpan(a, Math.Min(24, serverStream.Length - a)))}");
            Console.WriteLine($"  recebido: {Convert.ToHexString(received.Skip(a).Take(24).ToArray())}");
        }
    }

    private static List<byte[]> Split(byte[] stream, IReadOnlyDictionary<ushort, int> sizes, string label)
    {
        var list = new List<byte[]>();
        int off = 0;
        while (off + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, off);
            if (!sizes.TryGetValue(id, out int size) || off + size > stream.Length)
            {
                Console.WriteLine($"  ({label}: parou em 0x{off:x}, msgid 0x{id:x4} sem tamanho conhecido)");
                break;
            }
            list.Add(stream.AsSpan(off, size).ToArray());
            off += size;
        }
        return list;
    }
}
