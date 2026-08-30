using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Mapeia os campos do perfil do jogador procurando, nas mensagens decifradas, os
/// valores que o proprio cliente mostra no ecra.
///
/// E' o mesmo metodo que identificou o id da musica: em vez de adivinhar o significado
/// dos bytes, procura-se um numero conhecido e ve-se onde esta'. Com nivel, HP, MAX,
/// precisao, recorde e combo maximo ha' alvos que chegam para fixar quase toda a
/// estrutura.
/// </summary>
public static class ProfileDecoder
{
    public static void Run(string path, params (string Name, long Value)[] known)
    {
        var events = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[2].Length > 0)
            .Select(p => (Dir: p[0], Data: Convert.FromHexString(p[2])))
            .ToList();

        var stream = events.Where(e => e.Dir == "S2C").SelectMany(e => e.Data).ToArray();
        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        // mensagens que descrevem o jogador
        var wanted = new ushort[] { 0x00DE, 0x0043, 0x0025 };

        int pos = 3 + 47;
        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);
            int len;
            byte[] body;

            if (id == GameInfoFraming.MessageId)
            {
                int total;
                try { total = GameInfoFraming.ReadTotalLength(stream, pos, cipher, out var prefix); }
                catch { break; }
                int rest = total - PacketCodec.HeaderSize - GameInfoFraming.PrefixLength;
                if (pos + total > stream.Length) break;
                var tail = stream.AsSpan(pos + PacketCodec.HeaderSize + GameInfoFraming.PrefixLength, rest).ToArray();
                cipher.Decrypt(tail);
                pos += total;
                continue;
            }

            int? size = MessageSizes.FromServer(id, stream, pos);
            if (size is null || pos + size.Value > stream.Length) break;
            len = size.Value;
            body = Array.Empty<byte>();
            if (len >= PacketCodec.MinEncryptedSize)
            {
                body = stream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
            }

            if (wanted.Contains(id) && body.Length > 0)
            {
                Console.WriteLine($"=== 0x{id:x4} {SizeHarvester.MessageNames.GetValueOrDefault(id, "")} " +
                                  $"({body.Length} bytes) em 0x{pos:x4}");
                Dump(body);
                Locate(body, known);
                Console.WriteLine();
                // so' a primeira ocorrencia de cada tipo interessa
                wanted = wanted.Where(w => w != id).ToArray();
                if (wanted.Length == 0) return;
            }
            pos += len;
        }
    }

    private static void Dump(byte[] body)
    {
        for (int i = 0; i < body.Length; i += 16)
        {
            var c = body.AsSpan(i, Math.Min(16, body.Length - i)).ToArray();
            Console.WriteLine($"  +{i,4}  {string.Join(' ', c.Select(b => b.ToString("x2"))),-47}  " +
                new string(c.Select(b => b >= 0x20 && b <= 0x7E ? (char)b : '.').ToArray()));
        }
    }

    /// <summary>Procura cada valor conhecido como inteiro de 1, 2 e 4 bytes.</summary>
    private static void Locate(byte[] body, (string Name, long Value)[] known)
    {
        foreach (var (name, value) in known)
        {
            var hits = new List<string>();
            for (int i = 0; i < body.Length; i++)
            {
                if (value is >= 0 and <= 255 && body[i] == value) hits.Add($"+{i}(u8)");
                if (i + 2 <= body.Length && value is >= 0 and <= 65535 &&
                    BitConverter.ToUInt16(body, i) == value) hits.Add($"+{i}(u16)");
                if (i + 4 <= body.Length && value >= 0 && value <= uint.MaxValue &&
                    BitConverter.ToUInt32(body, i) == value) hits.Add($"+{i}(u32)");
            }
            Console.WriteLine($"    {name,-22} = {value,-9} : " +
                              (hits.Count > 0 ? string.Join("  ", hits) : "nao encontrado"));
        }
    }
}








