using System.Globalization;
using GrooveServer.Protocol;

namespace GrooveServer.Tools;

/// <summary>
/// Confere o enquadramento contra os limites dos segmentos TCP.
///
/// O inicio de um segmento e' verdade absoluta: alguma mensagem comeca ali. O contrario nao
/// se aplica â€” uma mensagem pode comecar a meio de um segmento, porque varias viajam
/// juntas. Portanto o teste e': **todo o inicio de segmento tem de coincidir com o inicio
/// de uma mensagem**. Onde deixar de coincidir, o tamanho da mensagem anterior esta' errado.
///
/// E' mais direto do que olhar para os corpos decifrados a ver se o texto faz sentido: da'
/// o offset exato e a mensagem culpada, em vez de "a partir daqui e' ruido".
/// </summary>
public static class SegmentCheck
{
    public static void Run(string caminho, string direcao = "S2C")
    {
        var eventos = File.ReadAllLines(caminho)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[2].Length > 0)
            .Select(p => (Dir: p[0], Time: double.Parse(p[1], CultureInfo.InvariantCulture),
                          Data: Convert.FromHexString(p[2])))
            .OrderBy(e => e.Time).ToList();

        var fluxo = eventos.Where(e => e.Dir == direcao).SelectMany(e => e.Data).ToArray();

        // Onde comeca cada segmento dentro do fluxo concatenado.
        var inicios = new HashSet<int>();
        int acc = 0;
        foreach (var e in eventos.Where(e => e.Dir == direcao))
        {
            inicios.Add(acc);
            acc += e.Data.Length;
        }

        Console.WriteLine($"{direcao}: {fluxo.Length} bytes em {inicios.Count} segmentos\n");

        var candidatos = new Dictionary<ushort, List<int>>();
        int pos = 0, n = 0;
        while (pos + 2 <= fluxo.Length)
        {
            ushort id = BitConverter.ToUInt16(fluxo, pos);

            // O GameInfoInf e' a unica mensagem de tamanho variavel, mas o comprimento
            // total vem EM CLARO no cabecalho, bytes 3..6 â€” nao e' preciso decifrar nada
            // para saltar por cima dele.
            if (direcao == "S2C" && id == GameInfoFraming.MessageId)
            {
                if (pos + 7 > fluxo.Length) break;
                int total = (int)BitConverter.ToUInt32(fluxo, pos + 3);
                if (total < 8 || pos + total > fluxo.Length)
                {
                    Console.WriteLine($"  +0x{pos:x6}  0x{id:x4} GameInfoInf com comprimento implausivel: {total}");
                    return;
                }
                pos += total;
                n++;
                continue;
            }

            int? tam = direcao == "S2C" ? MessageSizes.FromServer(id, fluxo, pos) : MessageSizes.FromClient(id);
            if (tam is null)
            {
                // Nao se desiste: se a mensagem comeca num segmento, o proximo inicio de
                // segmento e' um LIMITE SUPERIOR para o tamanho dela. Assume-se esse valor,
                // continua-se, e no fim ve-se se o palpite sobreviveu ao resto do fluxo.
                var seguinte = inicios.Where(i => i > pos).OrderBy(i => i).FirstOrDefault(-1);
                if (seguinte < 0)
                {
                    Console.WriteLine($"  +0x{pos:x6}  0x{id:x4} desconhecida e sem segmento a seguir");
                    return;
                }
                int limite = seguinte - pos;
                if (!candidatos.TryGetValue(id, out var lista)) candidatos[id] = lista = new List<int>();
                lista.Add(limite);
                Console.WriteLine($"  +0x{pos:x6}  0x{id:x4} desconhecida â€” no maximo {limite} bytes " +
                                  $"(comeca em segmento: {(inicios.Contains(pos) ? "sim" : "NAO")})");
                pos = seguinte;
                n++;
                continue;
            }

            pos += tam.Value;
            n++;

            // Se o proximo byte cair a meio de um segmento, tudo bem. O que nao pode
            // acontecer e' um segmento comecar onde nao comeca mensagem nenhuma.
            if (pos < fluxo.Length && !inicios.Contains(pos))
            {
                var proximoInicio = inicios.Where(i => i > pos - tam.Value).OrderBy(i => i).FirstOrDefault(-1);
                if (proximoInicio > 0 && proximoInicio < pos)
                {
                    Console.WriteLine(
                        $"  +0x{pos - tam.Value:x6}  0x{id:x4} tamanho {tam} passa por cima do inicio de " +
                        $"segmento em +0x{proximoInicio:x6} â€” o tamanho certo seria {proximoInicio - (pos - tam.Value)}");
                    return;
                }
            }
        }
        Console.WriteLine($"OK: {n} mensagens, enquadramento coerente com todos os segmentos");
        Resumo(candidatos);
    }

    /// <summary>Tamanhos candidatos recolhidos; o menor limite e' o mais informativo.</summary>
    private static void Resumo(Dictionary<ushort, List<int>> candidatos)
    {
        if (candidatos.Count == 0) return;
        Console.WriteLine("\n=== MENSAGENS SEM TAMANHO ===");
        foreach (var (id, lista) in candidatos.OrderBy(x => x.Key))
            Console.WriteLine($"  0x{id:x4}  visto {lista.Count}x, limites: {string.Join(", ", lista.Distinct().OrderBy(v => v))}" +
                              $"  -> provavel: {lista.Min()}");
    }

    private static void MostrarPista(byte[] fluxo, int pos, HashSet<int> inicios)
    {
        Console.WriteLine($"    comeca em inicio de segmento? {(inicios.Contains(pos) ? "SIM" : "NAO")}");
        int fim = Math.Min(fluxo.Length, pos + 24);
        Console.WriteLine($"    bytes: {Convert.ToHexString(fluxo, pos, fim - pos)}");
        var seguinte = inicios.Where(i => i > pos).OrderBy(i => i).FirstOrDefault(-1);
        if (seguinte > 0)
            Console.WriteLine($"    proximo inicio de segmento em +0x{seguinte:x6} " +
                              $"(ou seja, no maximo {seguinte - pos} bytes)");
    }
}

