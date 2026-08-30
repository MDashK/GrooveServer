using System.Globalization;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Net;

/// <summary>
/// Le' de uma captura as escolhas de musica que o cliente fez.
///
/// O id da musica vai em claro no cabecalho do <c>ChangeDiscReq</c>, mas a dificuldade
/// esta' no corpo cifrado — e' o byte 0. Para a ler e' preciso decifrar o fluxo do
/// cliente, o que exige uma subtileza: o <c>ConnectReq</c> vai EM CLARO apesar de ter
/// 23 bytes, porque o cliente envia-o antes de receber a chave. Passa-lo pela cifra
/// adianta o estado 16 bytes e faz sair ruido de tudo o resto.
/// </summary>
public static class ClientChoices
{
    public readonly record struct Choice(uint Song, byte Difficulty)
    {
        public override string ToString() => new SongKey(Song, Difficulty).ToString();
    }

    /// <summary>
    /// A escolha em vigor em cada arranque, uma por <c>StartReq</c>.
    ///
    /// Emparelhar por indice — a n-esima escolha com o n-esimo arranque — parte-se assim
    /// que o jogador selecione uma musica e nao a arranque: as listas desalinham e tudo
    /// o que vem a seguir fica etiquetado com a dificuldade errada, sem dar erro. Numa
    /// sessao longa isso acontece de certeza.
    ///
    /// Aqui percorre-se o fluxo por ordem e cada arranque leva a ultima escolha feita
    /// antes dele, que e' o que o cliente de facto tinha selecionado.
    /// </summary>
    public static List<Choice> AtEachStart(string capturePath)
    {
        var result = new List<Choice>();
        Choice? current = null;
        foreach (var (id, song, difficulty) in Walk(capturePath))
        {
            if (id == RequestId.ChangeDiscReq) current = new Choice(song, difficulty);
            else if (id == RequestId.StartReq && current is Choice c) result.Add(c);
            else if (id == RequestId.StartReq)
                Console.WriteLine("  AVISO: arranque sem escolha anterior; ignorado");
        }
        return result;
    }

    /// <summary>Percorre os pacotes do cliente, decifrando-os, e devolve os relevantes.</summary>
    private static IEnumerable<(ushort Id, uint Song, byte Difficulty)> Walk(string capturePath)
    {
        foreach (var item in Read(capturePath, includeStarts: true)) yield return item;
    }

    private static List<(ushort Id, uint Song, byte Difficulty)> Read(string capturePath, bool includeStarts)
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

        var server = events.Where(e => e.Dir == "S2C").SelectMany(e => e.Data).ToArray();
        var client = events.Where(e => e.Dir == "C2S").SelectMany(e => e.Data).ToArray();

        var result = new List<(ushort Id, uint Song, byte Difficulty)>();
        if (server.Length < 50 || BitConverter.ToUInt16(server, 3) != 0x000A) return result;

        var key = PacketCodec.TransformSessionKey(server.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        int pos = 0;
        while (pos + 2 <= client.Length)
        {
            ushort id = BitConverter.ToUInt16(client, pos);
            int? size = MessageSizes.FromClient(id);
            if (size is null || pos + size.Value > client.Length) break;
            int len = size.Value;

            // ConnectReq (0x000A) vai em claro; nao passa pela cifra
            if (len >= PacketCodec.MinEncryptedSize && id != 0x000A)
            {
                var body = client.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
                if (id == RequestId.ChangeDiscReq && body.Length >= 1)
                    result.Add((id, BitConverter.ToUInt32(client, pos + 3), body[0]));
                else if (includeStarts && id == RequestId.StartReq)
                    result.Add((id, 0u, 0));
            }
            pos += len;
        }
        return result;
    }
}
