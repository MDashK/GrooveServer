using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Le' as credenciais de uma captura, para confirmar a reversao contra valores conhecidos.
/// </summary>
public static class CredentialTest
{
    public static void Run(string caminho)
    {
        var eventos = File.ReadAllLines(caminho)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[2].Length > 0)
            .Select(p => (Dir: p[0], Time: double.Parse(p[1], CultureInfo.InvariantCulture),
                          Data: Convert.FromHexString(p[2])))
            .OrderBy(e => e.Time).ToList();

        var servidor = eventos.Where(e => e.Dir == "S2C").SelectMany(e => e.Data).ToArray();
        var cliente = eventos.Where(e => e.Dir == "C2S").SelectMany(e => e.Data).ToArray();

        if (servidor.Length < 50 || BitConverter.ToUInt16(servidor, 3) != 0x000A)
        { Console.WriteLine("sem ConnectAck; nao ha' chave de sessao"); return; }

        var chave = PacketCodec.TransformSessionKey(servidor.AsSpan(3 + 7, 32));
        var palavras = new uint[8];
        for (int w = 0; w < 8; w++) palavras[w] = BitConverter.ToUInt32(chave, w * 4);
        var cifra = new DjMaxCipher(BitConverter.ToUInt16(chave, 0x1C), 0u, palavras);

        int pos = 0, achados = 0;
        while (pos + 2 <= cliente.Length)
        {
            ushort id = BitConverter.ToUInt16(cliente, pos);
            int? tam = MessageSizes.FromClient(id);
            if (tam is null || pos + tam.Value > cliente.Length) break;

            var pacote = cliente.AsSpan(pos, tam.Value).ToArray();
            if (tam.Value >= PacketCodec.MinEncryptedSize && id != 0x000A)
            {
                var corpo = pacote.AsSpan(7).ToArray();
                cifra.Decrypt(corpo);
                corpo.CopyTo(pacote, 7);
            }

            if (id == Credentials.MessageId)
            {
                var (u, p) = Credentials.Ler(pacote);
                uint k = Credentials.Crc32(pacote.AsSpan(Credentials.SessionKeyOffset,
                                                         Credentials.SessionKeyLength));
                Console.WriteLine($"AuthenticateInACCReq em +{pos}");
                Console.WriteLine($"  chave CRC32 = 0x{k:X8}");
                Console.WriteLine($"  utilizador  = \"{u}\"");
                Console.WriteLine($"  password    = \"{p}\"");
                achados++;
            }
            pos += tam.Value;
        }
        if (achados == 0) Console.WriteLine("nenhum AuthenticateInACCReq nesta captura");
    }
}
