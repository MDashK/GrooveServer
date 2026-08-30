using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Decifra os pacotes que o CLIENTE envia e compara ocorrencias do mesmo tipo.
///
/// Serve para localizar campos por diferenca: escolhendo cinco musicas seguidas, os
/// cinco <c>ChangeDiscReq</c> so' diferem no que identifica a musica. Os bytes que
/// mudam sao o campo procurado — sem ser preciso reverter mais binario.
///
/// Cada direcao tem a sua propria cifra, ambas partindo da chave do ConnectAck, por
/// isso o fluxo do cliente decifra-se de forma independente do do servidor.
/// </summary>
public static class ClientDump
{
    public static void Run(string path, ushort msgId, int stream = -1)
    {
        var events = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[2].Length > 0)
            .Select(p => (Dir: p[0],
                          Time: double.Parse(p[1], CultureInfo.InvariantCulture),
                          Data: Convert.FromHexString(p[2])))
            .OrderBy(e => e.Time)
            .ToList();

        var serverStream = events.Where(e => e.Dir == "S2C").SelectMany(e => e.Data).ToArray();
        var clientStream = events.Where(e => e.Dir == "C2S").SelectMany(e => e.Data).ToArray();

        if (serverStream.Length < 50 || BitConverter.ToUInt16(serverStream, 3) != 0x000A)
        { Console.WriteLine("sem ConnectAck no fluxo do servidor; nao ha' chave"); return; }

        var key = PacketCodec.TransformSessionKey(serverStream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);
        Console.WriteLine($"chave: {Convert.ToHexString(key)}\n");

        var matches = new List<(int Index, byte[] Header, byte[] Body)>();
        var inventario = new Dictionary<ushort, int>();
        int pos = 0, index = 0;
        while (pos + 2 <= clientStream.Length)
        {
            ushort id = BitConverter.ToUInt16(clientStream, pos);
            int? size = MessageSizes.FromClient(id);
            if (size is null) { Console.WriteLine($"0x{pos:x4}: msgid 0x{id:x4} sem tamanho — paragem"); break; }
            int len = size.Value;
            if (pos + len > clientStream.Length) break;

            var header = clientStream.AsSpan(pos, Math.Min(7, len)).ToArray();
            byte[] body = Array.Empty<byte>();

            // O ConnectReq vai EM CLARO apesar de ter 23 bytes: o cliente envia-o antes
            // de receber o ConnectAck, ou seja antes de ter a chave. Passa-lo pela cifra
            // adianta o estado 16 bytes e estraga a decifra de tudo o que vem a seguir —
            // era por isso que o fluxo do cliente saia sempre a ruido.
            bool encrypted = len >= PacketCodec.MinEncryptedSize && id != 0x000A;
            if (encrypted)
            {
                body = clientStream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
            }
            else if (len > 7)
            {
                body = clientStream.AsSpan(pos + 7, len - 7).ToArray();
            }
            if (id == msgId) matches.Add((index, header, body));
            inventario[id] = inventario.GetValueOrDefault(id) + 1;
            pos += len;
            index++;
        }

        // 0xFFFF nao e' um id real: pede o inventario de tudo o que o cliente enviou, para
        // nao ser preciso adivinhar ids um a um quando se procura um campo.
        if (msgId == 0xFFFF)
        {
            Console.WriteLine($"{inventario.Count} tipo(s) de mensagem do cliente:");
            foreach (var (id, n) in inventario.OrderByDescending(p => p.Value))
                Console.WriteLine($"  0x{id:x4} x{n}  {MessageSizes.FromClient(id)}B  " +
                                  SizeHarvester.MessageNames.GetValueOrDefault(id, ""));
            return;
        }

        Console.WriteLine($"{matches.Count} ocorrencia(s) de 0x{msgId:x4} " +
                          $"({SizeHarvester.MessageNames.GetValueOrDefault(msgId, "")})\n");
        if (matches.Count == 0) return;

        foreach (var (i, header, body) in matches)
        {
            Console.WriteLine($"  #{i}  cabecalho {Convert.ToHexString(header)}");
            Console.WriteLine($"      corpo     {Convert.ToHexString(body)}");
        }

        // Diferencas: os bytes que variam entre ocorrencias sao o que identifica a escolha.
        Console.WriteLine("\n=== bytes que variam entre ocorrencias ===");
        Report("cabecalho", matches.Select(m => m.Header).ToList());
        Report("corpo", matches.Select(m => m.Body).ToList());
    }

    private static void Report(string label, List<byte[]> items)
    {
        if (items.Count < 2) return;
        int len = items.Min(b => b.Length);
        var varying = new List<int>();
        for (int i = 0; i < len; i++)
            if (items.Any(b => b[i] != items[0][i])) varying.Add(i);

        if (varying.Count == 0) { Console.WriteLine($"  {label}: identico em todas"); return; }

        Console.WriteLine($"  {label}: variam os offsets {string.Join(", ", varying)}");
        foreach (var off in varying)
            Console.WriteLine($"    +{off,-3} : {string.Join("  ", items.Select(b => $"{b[off]:x2}"))}");

        // agrupar offsets contiguos e mostrar como inteiros, que e' como o campo se le'
        foreach (var group in GroupRuns(varying))
        {
            if (group.Count is < 2 or > 4) continue;
            var vals = items.Select(b =>
            {
                uint v = 0;
                for (int k = group.Count - 1; k >= 0; k--) v = (v << 8) | b[group[k]];
                return v;
            });
            Console.WriteLine($"    campo +{group[0]}..{group[^1]} como inteiro LE: {string.Join(", ", vals)}");
        }
    }

    private static IEnumerable<List<int>> GroupRuns(List<int> offsets)
    {
        var run = new List<int>();
        foreach (var o in offsets)
        {
            if (run.Count > 0 && o != run[^1] + 1) { yield return run; run = new List<int>(); }
            run.Add(o);
        }
        if (run.Count > 0) yield return run;
    }
}
