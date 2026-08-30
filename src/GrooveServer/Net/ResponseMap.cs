using System.Globalization;
using System.Net;
using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Net;

/// <summary>
/// O que o servidor original respondeu a cada pedido do cliente, em texto em claro.
///
/// E' a evolucao do replay linear. O replay repete os passos por ordem, o que obriga o
/// jogador a repetir exatamente a sessao gravada â€” carregar em ESC fora de tempo, ou
/// escolher outra musica, deixa o guiao dessincronizado e o cliente pendurado.
///
/// Aqui indexa-se pelo message id do pedido: chega um <c>LeaveRoomReq</c>, responde-se
/// com o que o servidor original respondeu a um <c>LeaveRoomReq</c>, esteja o jogador
/// onde estiver. As respostas ficam guardadas DECIFRADAS, para poderem ser recifradas
/// com a chave da sessao em curso â€” que e' nossa e diferente da gravada.
/// </summary>
public sealed class ResponseMap
{
    /// <summary>Uma mensagem do servidor: cabecalho de 7 bytes + corpo em claro.</summary>
    public readonly record struct Message(ushort Id, byte[] Header, byte[] Body)
    {
        public int TotalLength => Header.Length + Body.Length;
    }

    /// <summary>
    /// Respostas por tipo de pedido, guardadas POR OCORRENCIA e nao agregadas.
    ///
    /// O mesmo pedido aparece muitas vezes numa sessao com respostas diferentes: um
    /// ping normalmente nao gera resposta nenhuma, mas de vez em quando o servidor
    /// aproveita para enviar o estado da sala. Juntar tudo num so' balde faria cada ping
    /// disparar uma enxurrada de mensagens repetidas.
    ///
    /// Guardando a lista de cada ocorrencia, a n-esima vez que o cliente faz o pedido X
    /// recebe o que o servidor original respondeu a' n-esima vez â€” e quando as
    /// ocorrencias se esgotam, repete-se a ultima.
    /// </summary>
    private readonly Dictionary<ushort, List<List<Message>>> _byRequest = new();

    /// <summary>
    /// Respostas indexadas pelo pedido E pelos 4 bytes que ele leva no cabecalho [3..6].
    ///
    /// A indexacao por ocorrencia assume que o jogador repete a sessao pela mesma ordem.
    /// Isso cai quando ele escolhe entre varias coisas â€” no course mode os dois primeiros
    /// bytes dizem QUAL course, e ha' uma resposta gravada para cada um.
    ///
    /// Usam-se os QUATRO e nao so' os dois primeiros porque os dois seguintes distinguem a
    /// ACCAO: navegar na lista devolve o ranking inteiro (`0x0084` + `0x0086`), confirmar
    /// para arrancar devolve so' o `0x0086`. Os pedidos so' diferem ai':
    ///     `0000 2040` navegar  ->  0x0084 + 0x0086
    ///     `0000 F649` arrancar ->  0x0086
    /// Responder ao arranque com o pacote de navegacao deixava o cliente a' espera.
    /// </summary>
    private readonly Dictionary<(ushort Id, uint Key), List<Message>> _byKey = new();

    /// <summary>Chaves gravadas para um pedido, para diagnostico.</summary>
    public IEnumerable<(uint Chave, IReadOnlyList<Message> Respostas)> ChavesDe(ushort requestId) =>
        _byKey.Where(kv => kv.Key.Id == requestId)
              .Select(kv => (kv.Key.Key, (IReadOnlyList<Message>)kv.Value));

    /// <summary>Resposta gravada para este pedido com esta chave, ou vazia se nao houver.</summary>
    public IReadOnlyList<Message> ForKey(ushort requestId, uint key) =>
        _byKey.TryGetValue((requestId, key), out var m) ? m : Array.Empty<Message>();

    /// <summary>Todos os baldes pela ordem em que apareceram na gravacao.</summary>
    private readonly List<List<Message>> _ordem = new();

    /// <summary>
    /// Conjuntos de respostas que contem esta mensagem, PELA ORDEM DA GRAVACAO.
    ///
    /// Serve para sequencias em que o balde do pedido nao e' de confiar. No course mode, a
    /// primeira musica leva um <c>0x00F0</c> entre o <c>StartReq</c> e a resposta, o que
    /// atira o grupo de arranque para o balde errado; as outras duas nao. Procurar pelo
    /// conteudo â€” quem traz o <c>GameInfoInf</c> â€” apanha as tres na ordem certa.
    /// </summary>
    /// <summary>
    /// A primeira ocorrencia gravada de uma mensagem do servidor, sem olhar ao pedido a que
    /// o agrupamento a atribuiu. Serve para as respostas que o agrupamento por proximidade
    /// poe debaixo do pedido errado — ver o 0x0088 em ResponsiveSession.
    /// </summary>
    public Message? PrimeiraDe(ushort messageId) =>
        AllSetsContaining(messageId).SelectMany(g => g)
            .Select(m => (Message?)m).FirstOrDefault(m => m!.Value.Id == messageId);

    /// <summary>Todas as ocorrencias gravadas de uma mensagem, pela ordem da gravacao.</summary>
    public IEnumerable<Message> TodasDe(ushort messageId) =>
        AllSetsContaining(messageId).SelectMany(g => g).Where(m => m.Id == messageId);

    public IReadOnlyList<IReadOnlyList<Message>> AllSetsContaining(ushort messageId) =>
        _ordem.Where(b => b.Any(m => m.Id == messageId)).ToList();

    /// <summary>Mensagens enviadas antes de o cliente pedir seja o que for.</summary>
    public IReadOnlyList<Message> Greeting { get; private set; } = Array.Empty<Message>();

    /// <summary>
    /// O ConnectAck gravado, em bruto (47 bytes).
    ///
    /// Reenvia-se tal e qual em vez de gerar um novo. Os seus 32 bytes a partir do
    /// offset 7 sao o material da chave de sessao, e a forma exata de que o cliente
    /// deriva a chave nao esta' toda esclarecida â€” sabe-se que os dois ultimos bytes sao
    /// sempre zero, o que sugere que ha' mais estrutura ali do que a que reproduzimos.
    /// Reutilizar os bytes originais evita o problema por completo: a chave e' a mesma
    /// que ja' se sabe funcionar.
    ///
    /// O endereco que ele anuncia nao importa: o modo replay mostrou que o cliente se
    /// liga ao canal pelo endereco do ChannelInfoInf, nao por este.
    /// </summary>
    public byte[] ConnectAck { get; private set; } = Array.Empty<byte>();

    /// <summary>
    /// Resposta a' ocorrencia <paramref name="occurrence"/> (base 0) do pedido.
    /// Esgotadas as ocorrencias gravadas, repete-se a ultima.
    /// </summary>
    public IReadOnlyList<Message> For(ushort requestId, int occurrence)
    {
        if (!_byRequest.TryGetValue(requestId, out var list) || list.Count == 0)
            return Array.Empty<Message>();
        return list[Math.Min(occurrence, list.Count - 1)];
    }

    public IEnumerable<ushort> KnownRequests => _byRequest.Keys;

    /// <summary>Esta gravacao chegou a ver este pedido?</summary>
    /// <remarks>
    /// Distingue "o servidor original nao respondeu a isto" de "esta sessao nunca fez
    /// isto". Sao coisas diferentes: a primeira e' para respeitar, a segunda e' um buraco
    /// a preencher com outra gravacao.
    /// </remarks>
    public bool HasRequest(ushort requestId) => _byRequest.ContainsKey(requestId);

    /// <summary>Quantas vezes o pedido apareceu na gravacao.</summary>
    public int OccurrencesOf(ushort requestId) =>
        _byRequest.TryGetValue(requestId, out var list) ? list.Count : 0;

    /// <summary>
    /// Primeira ocorrencia do pedido que teve resposta, ignorando as vazias.
    ///
    /// Ha' respostas que na gravacao calharam numa ocorrencia tardia de um pedido
    /// periodico â€” o fim de musica, por exemplo, veio a' sexta vez que o cliente enviou
    /// 0x0072. Contar ocorrencias nao serve nesses casos, porque a sessao de agora tem
    /// outro ritmo; e' preciso poder ir buscar a resposta pelo seu significado.
    /// </summary>
    /// <summary>
    /// O conjunto gravado que contem uma dada mensagem, venha ele de que pedido vier.
    ///
    /// Ha' respostas cujo balde nao corresponde ao pedido que as provocou: o servidor
    /// responde alguns milissegundos depois e, se o cliente entretanto mandou outra
    /// coisa, a resposta fica agrupada nessa. O fecho de musica e' o caso â€” sai a seguir
    /// ao <c>PlayOverReq</c> mas fica no balde do estado periodico que passou pelo meio.
    /// Procurar pela mensagem em vez de pelo pedido resolve.
    /// </summary>
    public IReadOnlyList<Message> FindSetContaining(ushort messageId)
    {
        foreach (var occurrences in _byRequest.Values)
            foreach (var bucket in occurrences)
                if (bucket.Any(m => m.Id == messageId)) return bucket;
        return Array.Empty<Message>();
    }

    public IReadOnlyList<Message> FirstNonEmpty(ushort requestId)
    {
        if (!_byRequest.TryGetValue(requestId, out var list)) return Array.Empty<Message>();
        foreach (var bucket in list) if (bucket.Count > 0) return bucket;
        return Array.Empty<Message>();
    }

    /// <summary>
    /// Nome do ficheiro de onde esta gravacao veio, sem caminho.
    ///
    /// Serve para se poder indicar uma gravacao PELO NOME em vez de a descobrir por
    /// conteudo. Nem tudo se distingue pelo que la' esta' dentro: uma sessao que fez um
    /// course e depois uma musica de free mode tem os dois tipos de arranque misturados, e
    /// o mapa nao guarda em que sala cada balde foi gravado.
    /// </summary>
    public string Nome { get; private set; } = "";

    public static ResponseMap Load(string path, IPAddress? rewriteFrom = null, IPAddress? rewriteTo = null)
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

        // decifrar todo o fluxo do servidor, respeitando o enquadramento
        var messages = DecryptAll(serverStream);
        var connectAck = serverStream.AsSpan(3, 47).ToArray();

        if (rewriteFrom is not null && rewriteTo is not null)
        {
            var from = rewriteFrom.GetAddressBytes();
            var to = rewriteTo.GetAddressBytes();
            int n = 0;
            foreach (var located in messages)
            {
                var body = located.Message.Body;
                for (int i = 0; i + from.Length <= body.Length; i++)
                {
                    bool hit = true;
                    for (int j = 0; j < from.Length; j++) if (body[i + j] != from[j]) { hit = false; break; }
                    if (hit) { to.CopyTo(body, i); n++; i += from.Length - 1; }
                }
            }
            if (Config.Verboso)
                Console.WriteLine($"  {Path.GetFileName(path)}: {n} address(es) rewritten in the replies");
        }

        // alinhar respostas com pedidos, pela ordem temporal dos segmentos
        var map = new ResponseMap();
        var clientIds = SplitIds(clientStream, MessageSizes.ClientToServer);

        int msgIdx = 0, clientIdx = 0;
        var greeting = new List<Message>();
        var pending = greeting;
        int serverBytesSeen = 0;

        foreach (var e in events)
        {
            if (e.Dir == "S2C")
            {
                serverBytesSeen += e.Data.Length;
                while (msgIdx < messages.Count &&
                       messages[msgIdx].EndOffset <= serverBytesSeen)
                {
                    pending.Add(messages[msgIdx].Message);
                    msgIdx++;
                }
            }
            else
            {
                // um segmento pode trazer varios pedidos colados; cada um abre um balde
                int consumed = 0;
                while (clientIdx < clientIds.Count)
                {
                    ushort id = clientIds[clientIdx];
                    int size = MessageSizes.FromClient(id) ?? 0;
                    if (size == 0 || consumed + size > e.Data.Length) break;
                    if (!map._byRequest.TryGetValue(id, out var occurrences))
                        map._byRequest[id] = occurrences = new List<List<Message>>();
                    var bucket = new List<Message>();
                    occurrences.Add(bucket);
                    map._ordem.Add(bucket);
                    pending = bucket;

                    // Indexar tambem pelos 2 bytes que o pedido leva no cabecalho [3..4].
                    // Contar ocorrencias nao chega quando o jogador escolhe uma de varias
                    // coisas: no course mode esses bytes sao o course pedido, e servir a
                    // n-esima resposta gravada em vez da que corresponde ao course faz o
                    // cliente nao reconhecer a resposta e voltar a perguntar, sem fim.
                    //
                    // O cabecalho vai sempre em claro, por isso le-se direto do segmento.
                    if (consumed + 7 <= e.Data.Length)
                    {
                        uint chave = BitConverter.ToUInt32(e.Data, consumed + 3) & 0x00FFFFFFu;
                        map._byKey[(id, chave)] = bucket;
                    }

                    consumed += size;
                    clientIdx++;
                }
            }
        }
        map.Greeting = greeting;
        map.ConnectAck = connectAck;

        if (Config.Verboso)
        {
            Console.WriteLine($"  {Path.GetFileName(path)}: {messages.Count} server messages, " +
                              $"{map._byRequest.Count} request types mapped");
            foreach (var (reqId, occurrences) in map._byRequest.OrderBy(k => k.Key))
            {
                var withReplies = occurrences.Where(o => o.Count > 0).ToList();
                if (withReplies.Count == 0) continue;
                var ids = withReplies.SelectMany(o => o).Select(m => $"0x{m.Id:x2}").Distinct();
                Console.WriteLine($"     0x{reqId:x4} x{occurrences.Count} -> " +
                                  $"{withReplies.Count} with a reply: {string.Join(" ", ids)}");
            }
        }
        map.Nome = Path.GetFileName(path);
        return map;
    }

    private sealed record Located(Message Message, int EndOffset);

    private static List<Located> DecryptAll(byte[] stream)
    {
        if (stream.Length < 3 + 47 || BitConverter.ToUInt16(stream, 3) != 0x000A)
            throw new InvalidOperationException("captura nao comeca com hello + ConnectAck");

        var key = PacketCodec.TransformSessionKey(stream.AsSpan(3 + 7, 32));
        var words = new uint[8];
        for (int w = 0; w < 8; w++) words[w] = BitConverter.ToUInt32(key, w * 4);
        var cipher = new DjMaxCipher(BitConverter.ToUInt16(key, 0x1C), 0u, words);

        var result = new List<Located>();

        // Comecar DEPOIS do hello e do ConnectAck. Ambos vao em claro â€” o cliente so'
        // arma as cifras depois de processar o ConnectAck â€” por isso passa-los pela
        // cifra adiantaria o estado indevidamente e estragaria tudo o que vem a seguir.
        // Sao tambem as duas mensagens que o servidor gera de novo em cada sessao.
        int pos = 3 + 47;

        while (pos + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, pos);

            // O GameInfoInf e' a unica mensagem de tamanho variavel: o comprimento vem
            // cifrado no proprio corpo, por isso e' preciso decifrar o prefixo primeiro.
            if (id == GameInfoFraming.MessageId)
            {
                int total;
                byte[] prefix;
                try { total = GameInfoFraming.ReadTotalLength(stream, pos, cipher, out prefix); }
                catch { break; }
                int rest = total - PacketCodec.HeaderSize - GameInfoFraming.PrefixLength;
                if (pos + total > stream.Length) break;

                var tail = stream.AsSpan(pos + PacketCodec.HeaderSize + GameInfoFraming.PrefixLength, rest).ToArray();
                cipher.Decrypt(tail);

                var full = new byte[prefix.Length + tail.Length];
                prefix.CopyTo(full, 0);
                tail.CopyTo(full, prefix.Length);

                var hdr = stream.AsSpan(pos, PacketCodec.HeaderSize).ToArray();
                result.Add(new Located(new Message(id, hdr, full), pos + total));
                pos += total;
                continue;
            }

            int? size = MessageSizes.FromServer(id, stream, pos);
            if (size is null || pos + size.Value > stream.Length) break;
            int len = size.Value;

            var header = stream.AsSpan(pos, Math.Min(7, len)).ToArray();
            byte[] body = Array.Empty<byte>();
            if (len >= PacketCodec.MinEncryptedSize)
            {
                body = stream.AsSpan(pos + 7, len - 7).ToArray();
                cipher.Decrypt(body);
            }
            result.Add(new Located(new Message(id, header, body), pos + len));
            pos += len;
        }
        return result;
    }

    private static List<ushort> SplitIds(byte[] stream, IReadOnlyDictionary<ushort, int> sizes)
    {
        var ids = new List<ushort>();
        int off = 0;
        while (off + 2 <= stream.Length)
        {
            ushort id = BitConverter.ToUInt16(stream, off);
            if (!sizes.TryGetValue(id, out int size) || off + size > stream.Length) break;
            ids.Add(id);
            off += size;
        }
        return ids;
    }
}


