using System.Globalization;
using System.Net;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Net;

/// <summary>
/// Sessao gravada, reproduzida como se fosse o servidor original.
///
/// A ideia que torna isto possivel: o <c>ConnectAck</c> gravado transporta a chave de
/// sessao usada naquela sessao. Reenviando-o tal e qual, o cliente deriva exatamente a
/// mesma chave — e todos os pacotes que o servidor original enviou a seguir, ja'
/// cifrados com ela, decifram corretamente no cliente de hoje.
///
/// Isso permite conduzir o cliente pelo login e ate' ao lobby sem conhecer o layout
/// interno de nenhuma mensagem. Nao e' um servidor a serio — nao reage ao que o cliente
/// pede, apenas repete o guiao — mas valida toda a pilha (enquadramento, cifra,
/// sequencia) contra o cliente real.
///
/// O guiao e' dividido em passos: cada passo e' o conjunto de bytes que o servidor
/// original enviou antes de esperar pelo pacote seguinte do cliente.
/// </summary>
public sealed class ReplaySource
{
    /// <summary>Bytes a enviar de uma vez, antes de esperar pelo cliente.</summary>
    public IReadOnlyList<byte[]> Steps { get; }

    private ReplaySource(IReadOnlyList<byte[]> steps) => Steps = steps;

    /// <summary>
    /// Carrega de um ficheiro `DIR\ttempo\thex`, uma linha por segmento TCP.
    /// Os segmentos do servidor que ocorrem entre dois pacotes do cliente formam um passo.
    /// </summary>
    /// <param name="rewriteIp">
    /// Se indicado, substitui o endereco do servidor original pelo nosso dentro do
    /// payload cifrado.
    ///
    /// E' indispensavel: a mensagem <c>ChannelInfoInf</c> traz a lista de canais com o
    /// endereco de cada um, e e' para la' que o cliente se liga ao escolher um modo.
    /// Sem reescrever isto, o cliente sai do nosso servidor e volta ao original.
    ///
    /// O <c>ConnectAck</c> tambem contem o endereco, mas NAO pode ser tocado: sao os
    /// mesmos bytes de que o cliente deriva a chave da cifra. Altera-los quebraria a
    /// decifra de tudo o resto.
    /// </param>
    public static ReplaySource Load(string path, (IPAddress From, IPAddress To)? rewriteIp = null)
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

        var steps = new List<byte[]>();
        var pending = new List<byte>();
        foreach (var e in events)
        {
            if (e.Dir == "S2C") pending.AddRange(e.Data);
            else
            {
                // chegou um pacote do cliente: fecha o passo atual (mesmo que vazio,
                // para manter o alinhamento entre pedidos e respostas)
                steps.Add(pending.ToArray());
                pending.Clear();
            }
        }
        steps.Add(pending.ToArray());

        if (rewriteIp is { } rw)
        {
            var full = steps.SelectMany(s => s).ToArray();
            int changed = RewriteAddress(full, rw.From, rw.To);
            Console.WriteLine($"  {Path.GetFileName(path)}: {changed} ocorrencia(s) de " +
                              $"{rw.From} reescritas para {rw.To}");
            // repartir pelo mesmo recorte de passos
            int off = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                int n = steps[i].Length;
                steps[i] = full.AsSpan(off, n).ToArray();
                off += n;
            }
        }
        return new ReplaySource(steps);
    }

    /// <summary>
    /// Decifra o fluxo do servidor, troca o endereco onde aparecer e volta a cifrar.
    ///
    /// A cifra realimenta-se com o texto em claro, por isso alterar um byte muda todo o
    /// texto cifrado a partir dai'. Nao ha' problema desde que se recifre o fluxo inteiro
    /// de forma coerente: o cliente decifra-o na mesma, porque parte da mesma chave.
    /// </summary>
    private static int RewriteAddress(byte[] stream, IPAddress from, IPAddress to)
    {
        var needle = from.GetAddressBytes();
        var replacement = to.GetAddressBytes();

        if (stream.Length < 3 + 47 || BitConverter.ToUInt16(stream, 3) != 0x000A)
            throw new InvalidOperationException(
                "o fluxo nao comeca com hello + ConnectAck; sem isso nao ha' chave para decifrar");

        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        uint chain = BitConverter.ToUInt16(key, 0x1C);

        // 1a passagem: decifrar cada corpo e guardar
        var decipher = new DjMaxCipher(chain, 0u, words);
        var bodies = new List<(int Offset, byte[] Body)>();
        int pos = 3 + 47, changed = 0;
        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);
            int? size = MessageSizes.FromServer(id, stream, pos);
            if (size is null || pos + size.Value > stream.Length) break;
            int len = size.Value;
            if (len >= PacketCodec.MinEncryptedSize)
            {
                var body = stream.AsSpan(pos + 7, len - 7).ToArray();
                decipher.Decrypt(body);
                bodies.Add((pos + 7, body));
            }
            pos += len;
        }

        // 2a passagem: substituir o endereco no texto em claro
        foreach (var (_, body) in bodies)
            for (int i = 0; i + needle.Length <= body.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++) if (body[i + j] != needle[j]) { hit = false; break; }
                if (hit) { replacement.CopyTo(body, i); changed++; i += needle.Length - 1; }
            }

        // 3a passagem: recifrar de raiz, com o mesmo estado inicial
        var recipher = new DjMaxCipher(chain, 0u, words);
        foreach (var (offset, body) in bodies)
        {
            recipher.Encrypt(body);
            body.CopyTo(stream.AsSpan(offset));
        }
        return changed;
    }

    public int TotalBytes => Steps.Sum(s => s.Length);
}
