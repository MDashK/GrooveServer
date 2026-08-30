using GrooveServer.Net;

namespace GrooveServer.Tools;

/// <summary>
/// Confirma que o <c>InventoryInfoInf</c> e' a concatenacao das cinco seccoes de
/// inventario, comparando-o com as mensagens que enviam cada uma em separado.
///
/// A soma dos corpos das mensagens por categoria — default 192, event 128, shop 240,
/// mount 64, present 120 — da' exatamente 744, que e' o tamanho do corpo do
/// <c>InventoryInfoInf</c>. Se as seccoes baterem byte a byte, fica provado onde cada
/// categoria comeca — e escrever itens no sitio errado deixa de acontecer.
/// </summary>
public static class InventoryLayout
{
    private static readonly (ushort Id, string Nome, int Tamanho)[] Seccoes =
    {
        (0x002A, "default", 192),
        (0x002B, "event",   128),
        (0x002C, "shop",    240),
        (0x002D, "mount",    64),
        (0x002E, "present", 120),
    };

    public static void Run(params string[] capturas)
    {
        byte[]? inventario = null;
        var partes = new Dictionary<ushort, byte[]>();

        foreach (var caminho in capturas)
        {
            if (!File.Exists(caminho)) continue;
            var map = ResponseMap.Load(caminho, null, null);
            foreach (var reqId in map.KnownRequests)
                for (int i = 0; i < map.OccurrencesOf(reqId); i++)
                    foreach (var m in map.For(reqId, i))
                    {
                        if (m.Id == 0x0044 && inventario is null) inventario = m.Body;
                        if (Seccoes.Any(s => s.Id == m.Id) && !partes.ContainsKey(m.Id))
                            partes[m.Id] = m.Body;
                    }
        }

        if (inventario is null) { Console.WriteLine("sem InventoryInfoInf nas capturas"); return; }
        Console.WriteLine($"InventoryInfoInf: {inventario.Length} bytes");
        Console.WriteLine($"soma das seccoes: {Seccoes.Sum(s => s.Tamanho)}\n");

        int offset = 0;
        foreach (var (id, nome, tamanho) in Seccoes)
        {
            string estado;
            if (!partes.TryGetValue(id, out var parte)) estado = "(nao apareceu nas capturas)";
            else if (parte.Length != tamanho) estado = $"(tamanho inesperado: {parte.Length})";
            else
            {
                int diff = -1;
                for (int i = 0; i < tamanho && offset + i < inventario.Length; i++)
                    if (parte[i] != inventario[offset + i]) { diff = i; break; }
                estado = diff < 0 ? "CONFERE byte a byte" : $"difere em +{diff}";
            }
            Console.WriteLine($"  0x{id:x4} {nome,-8} {offset,4}..{offset + tamanho - 1,-4} ({tamanho,3} B)  {estado}");
            offset += tamanho;
        }

        Console.WriteLine("\nonde estao os itens (pares nao vazios de 8 bytes):");
        for (int i = 0; i + 8 <= inventario.Length; i += 4)
        {
            uint a = BitConverter.ToUInt32(inventario, i);
            if (a is 0xFFFFFFFF or 0) continue;
            uint b = BitConverter.ToUInt32(inventario, i + 4);
            if (b == 0xFFFFFFFF) continue;
            string seccao = SeccaoDe(i);
            Console.WriteLine($"  +{i,4}  item {a,-10} instancia {b,-12}  seccao {seccao}");
        }
    }

    private static string SeccaoDe(int offset)
    {
        int acc = 0;
        foreach (var (_, nome, tamanho) in Seccoes)
        {
            if (offset >= acc && offset < acc + tamanho) return $"{nome} (+{offset - acc})";
            acc += tamanho;
        }
        return "fora";
    }
}
