using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Localiza onde a decifra do fluxo do cliente perde o sincronismo.
///
/// O enquadramento pode estar correto e a cifra na mesma descarrilar: basta que um
/// pacote seja cifrado quando nao devia, ou o contrario. O estado avanca a mais ou a
/// menos e tudo o que vem a seguir sai a ruido, sem que as fronteiras se percam.
///
/// Usa-se o <c>ChangeDiscReq</c> como sonda: o primeiro byte do corpo e' a dificuldade e
/// so' pode valer 0 a 4. O primeiro que sair fora disso marca o ponto — e imprimem-se os
/// pacotes anteriores, que sao os suspeitos.
/// </summary>
public static class DesyncFinder
{
    public static void Run(string path)
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

        var server = events.Where(e => e.Dir == "S2C").SelectMany(e => e.Data).ToArray();
        var client = events.Where(e => e.Dir == "C2S").SelectMany(e => e.Data).ToArray();

        var key = PacketCodec.TransformSessionKey(server.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        var history = new List<string>();
        int pos = 0, index = 0;
        while (pos + 2 <= client.Length)
        {
            ushort id = BitConverter.ToUInt16(client, pos);
            int? size = MessageSizes.FromClient(id);
            if (size is null) { Console.WriteLine($"#{index} 0x{pos:x6}: msgid 0x{id:x4} desconhecido"); return; }
            int len = size.Value;
            if (pos + len > client.Length) break;

            bool enc = len >= PacketCodec.MinEncryptedSize && id != 0x000A;
            byte[] body = Array.Empty<byte>();
            if (enc)
            {
                body = client.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
            }

            history.Add($"#{index,-5} 0x{pos:x6}  0x{id:x4} len={len,4} {(enc ? "cifrado" : "em claro")}" +
                        (body.Length > 0 ? $"  {Convert.ToHexString(body.AsSpan(0, Math.Min(12, body.Length)).ToArray())}" : ""));

            if (id == RequestId.ChangeDiscReq && body.Length >= 1 && body[0] > 4)
            {
                Console.WriteLine($"PRIMEIRA DESSINCRONIA em #{index}, offset 0x{pos:x6}");
                Console.WriteLine($"  dificuldade lida: {body[0]} (so' 0..4 sao validas)\n");
                Console.WriteLine("20 pacotes anteriores:");
                foreach (var h in history.TakeLast(21)) Console.WriteLine("  " + h);
                return;
            }
            pos += len;
            index++;
        }
        Console.WriteLine($"percorreu {index} pacotes sem dessincronia detetada");
    }
}
