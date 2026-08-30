using GrooveServer.Net;

namespace GrooveServer.Tools;

/// <summary>
/// A SONDA DOS DISCOS: poe um numero DISTINTO em cada id da coleccao para que o ecra os
/// identifique sozinho.
///
/// Foi assim que se mapeou o bloco de cima (0x0400..0x0408) e o cherry: cada icone mostra o
/// seu numero, e a correspondencia id -> icone sai sem ambiguidade. O ecra de coleccao e' a
/// unica fonte que da' isto — nem os `.pak` nem o despejo do cliente trazem a tabela.
///
/// O QUE FALTA CONFIRMAR (ver Protocol.DefaultItems.Discos):
///   * `0x0401` e `0x0402` — deduzidos como DEVIL e SAPPHIRE pelo desvio da folha de sprites;
///   * os ids do RUBY e do RAINBOW, que com esse desvio cairiam abaixo de 0x0400 e portanto
///     nao estao determinados;
///   * `0x0409..0x0411` — devem ser os nove discos de missao.
///
/// Correr, entrar no jogo e abrir a coleccao. Cada icone traz um numero: o numero diz o id.
/// A conta e' guardada como esta' antes de mexer, num ficheiro ao lado.
/// </summary>
public static class DiscProbe
{
    /// <summary>Primeiro id sondado. O numero mostrado e' <c>Base + (id - Primeiro)</c>.</summary>
    public const ushort Primeiro = 0x0400;
    public const ushort Ultimo = 0x042E;
    public const int Base = 90;

    public static void Run(string caminhoUsers, string conta, bool repor)
    {
        var store = new UserStore(caminhoUsers);
        var acc = store.Find(conta);
        if (acc is null)
        {
            Console.WriteLine($"conta '{conta}' nao existe em {caminhoUsers}");
            Console.WriteLine("contas: " + string.Join(", ", store.Accounts.Select(a => a.Name)));
            return;
        }

        var copia = caminhoUsers + $".antes-da-sonda-{conta}";
        if (repor)
        {
            if (!File.Exists(copia)) { Console.WriteLine($"nao ha' {copia} para repor"); return; }
            File.Copy(copia, caminhoUsers, overwrite: true);
            Console.WriteLine($"reposto {caminhoUsers} a partir de {copia}");
            return;
        }

        if (!File.Exists(copia))
        {
            File.Copy(caminhoUsers, copia);
            Console.WriteLine($"copia de seguranca: {copia}");
        }

        int n = 0;
        for (ushort id = Primeiro; id <= Ultimo; id++)
        {
            acc.DefaultItems[id.ToString()] = Base + (id - Primeiro);
            n++;
        }
        store.Save();

        Console.WriteLine($"sonda posta em {conta}: {n} ids de 0x{Primeiro:X4} a 0x{Ultimo:X4}");
        Console.WriteLine($"o numero no icone e' {Base} + (id - 0x{Primeiro:X4}); por exemplo:");
        foreach (ushort id in new ushort[] { 0x0400, 0x0401, 0x0402, 0x0403, 0x0420, 0x042E })
            Console.WriteLine($"   0x{id:X4} mostra {Base + (id - Primeiro),3}   " +
                              $"({Protocol.DefaultItems.Discos.Nome(id)})");
        Console.WriteLine();
        Console.WriteLine("entrar no jogo, abrir a coleccao e fotografar as duas paginas.");
        Console.WriteLine($"para desfazer:  GrooveServer.exe discprobe {conta} repor");
    }
}
