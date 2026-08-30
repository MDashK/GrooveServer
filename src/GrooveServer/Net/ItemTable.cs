namespace GrooveServer.Net;

/// <summary>
/// O que cada item da loja faz: bonus de experiencia, de MAX e de HP.
///
/// Vem do <c>itens.txt</c>, gerado do <c>System\shop\ItemStock.csv</c> do cliente. A chave e'
/// o id de catalogo (<c>base + wItem</c> do CSV), que sao os 16 bits BAIXOS do id que viaja no
/// protocolo.
///
/// CONFIRMADO CONTRA O JOGO. Com os tres itens permanentes da conta equipados, o painel de
/// perfil mostra "HP 195 (130+65)"; a tabela da'-lhes 15 + 30 + 20 = 65. Com os de 30 dias,
/// "260 (130+130)". E o texto de cada item no ecra de detalhe — "经验值+10%, HP+15, 永久,
/// 9000 MAX" — e' letra por letra a linha 35846 do CSV.
///
/// O HP e' aplicado pelo CLIENTE (ve-se no perfil sem o servidor mexer em nada). O EXP e o MAX
/// e' que nao: o cliente reporta o ganho base no <c>0x006F</c> e cabe ao servidor multiplicar.
/// </summary>
public sealed class ItemTable
{
    /// <param name="Exp">Bonus de experiencia, em PERCENTAGEM (coluna <c>exp</c>).</param>
    /// <param name="MaxPct">Bonus de MAX em PERCENTAGEM (coluna <c>max_amplify</c>).</param>
    /// <param name="MaxFixo">Bonus de MAX em valor FIXO (coluna <c>max_supremum</c>).</param>
    /// <param name="Hp">HP extra. Fica aqui por completude — quem o aplica e' o cliente.</param>
    /// <param name="Dias">Validade; 0 e' permanente.</param>
    /// <param name="UsoUnico">Descartavel (a coluna <c>countable</c> do CSV).</param>
    /// <param name="Seccao">
    /// A <c>section1</c> da loja. Decide a metade ALTA do id de catalogo — ver
    /// <see cref="AltoDoCatalogo"/>. Zero quando o item nao esta' no ItemStock.
    /// </param>
    public readonly record struct Item(uint Catalogo, string Nome, double Exp, double MaxPct,
                                       double MaxFixo, int Hp, int Dias, bool UsoUnico,
                                       int Seccao = 0);

    /// <summary>
    /// A metade ALTA do id de catalogo, que NAO e' constante — depende do tipo de item.
    ///
    /// O premio de um course era montado sempre com 2, e por isso um avatar sorteado chegava ao
    /// cliente como "seccao 2, item 2065", que nao existe: a caixa do premio no ecra de
    /// COURSE SUCCESS aparecia EM BRANCO. Foi assim que se deu por isto — o `Enjoy! DJMAX!` tem
    /// 2% de hipotese de sortear o `Cosmic_Boy` e calhou.
    ///
    /// Medido no inventario de uma conta a serio, cruzado com o ItemStock:
    ///
    /// | item | seccao | catalogo | alto |
    /// |---|---|---|---|
    /// | Elle (avatar) | 2.1.0 | 99416 | 1 |
    /// | PG-P (gear) | 4.1.0 | 107556 | 1 |
    /// | Red_Crystal (gear) | 4.2.0 | 108562 | 1 |
    /// | HP_Boost_LV1 (consumo) | 5.1.0 | 193537 | 2 |
    /// | MAX_Boost_LV1 (consumo) | 5.3.0 | 193793 | 2 |
    ///
    /// **As seccoes 1 (moedas) e 3 (HPPLUS, MAXSTRIKE, EXPPLUS) nao estao medidas** — nenhuma
    /// aparece em lotaria de course, por isso nao ha' amostra. Vao com 1, que e' o que as
    /// outras nao-consumiveis levam; se um dia forem precisas, mede-se.
    ///
    /// Item desconhecido vai com 2, que era o comportamento antigo e serve os 15 consumiveis
    /// que sao a esmagadora maioria do que os courses dao.
    /// </summary>
    public const uint AltoConsumivel = 2;
    public const uint AltoEquipavel = 1;
    public const int SeccaoConsumivel = 5;

    public uint AltoDoCatalogo(uint baixo) =>
        Get(baixo) is { Seccao: > 0 } i && i.Seccao != SeccaoConsumivel
            ? AltoEquipavel
            : AltoConsumivel;

    private readonly Dictionary<uint, Item> _porCatalogo = new();

    public int Count => _porCatalogo.Count;

    /// <summary>
    /// Procura pelos 16 bits BAIXOS: o inventario guarda o id completo (a metade alta e' a
    /// seccao) e a tabela do cliente so' conhece a metade baixa.
    /// </summary>
    public Item? Get(uint catalogo) =>
        _porCatalogo.TryGetValue(catalogo & 0xFFFF, out var i) ? i : null;

    public static ItemTable Load(string path)
    {
        var t = new ItemTable();
        if (!File.Exists(path)) return t;

        foreach (var raw in File.ReadAllLines(path))
        {
            var linha = raw.Trim();
            if (linha.Length == 0 || linha.StartsWith('#')) continue;

            var campos = linha.Split('\t');
            if (campos.Length < 3 || !uint.TryParse(campos[0], out uint cat)) continue;

            double exp = 0, maxPct = 0, maxFixo = 0;
            int hp = 0, dias = 0, uso = 0, seccao = 0;
            foreach (var campo in campos.Skip(2))
            {
                var p = campo.Split('=');
                if (p.Length != 2) continue;
                switch (p[0])
                {
                    case "exp": double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out exp); break;
                    case "maxpc": double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture, out maxPct); break;
                    case "maxfix": double.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                                   System.Globalization.CultureInfo.InvariantCulture, out maxFixo); break;
                    case "hp": int.TryParse(p[1], out hp); break;
                    case "dias": int.TryParse(p[1], out dias); break;
                    case "uso": int.TryParse(p[1], out uso); break;
                    case "seccao": int.TryParse(p[1], out seccao); break;
                }
            }
            _ = t._porCatalogo[cat] = new Item(cat, campos[1], exp, maxPct, maxFixo, hp, dias,
                                               uso != 0, seccao);
        }
        return t;
    }

    /// <summary>
    /// Quanto os itens EQUIPADOS somam. Os itens que a tabela nao conhece contam zero — mais
    /// vale nao dar bonus nenhum do que inventar um.
    ///
    /// PERCENTAGEM E VALOR FIXO SAO COISAS DIFERENTES, e o cliente diz qual e' qual no texto
    /// de cada item: o avatar diz "经验值+10%" com sinal de percentagem, o 黄金音符 diz
    /// "MAX+10" sem ele. Os nomes das colunas concordam — `max_amplify` multiplica,
    /// `max_supremum` soma. Somar os dois como percentagem, que foi a primeira tentativa,
    /// dava +2 de MAX numa musica onde o item promete +10.
    /// </summary>
    /// <summary>
    /// OS MAX BOOSTERS NAO TEM O EFEITO NOS DADOS — e' o unico buraco do ItemStock.csv.
    ///
    /// Os outros quatro descartaveis trazem-no na coluna certa: `HP_Boost` da' `hp` 30/100/200,
    /// `EXP_Boost` da' `exp` 50/200/500, `HP_RECOVERY` da' `hpboost` 0.1/0.3/0.5 e `SIGHT` da'
    /// `judgmentboost` 2/4/6. As tres linhas do `MAX_Boost` (`wItem` 257, 258, 259) tem
    /// **todas as colunas de efeito a zero**, e mesmo assim a loja promete "MAX +50%",
    /// "MAX +200%" e "MAX +500%".
    ///
    /// Sao os MESMOS numeros do EXP_Boost, que esses estao nos dados. Fica portanto a tabela
    /// aqui, a dizer o que a loja diz — nao ha' outra fonte, e deixa-los a zero era garantir
    /// que o item comprado nao faz nada.
    /// </summary>
    public static double PercentagemDoMaxBooster(uint catalogo) => (catalogo & 0xFFFF) switch
    {
        62721 => 50,     // MAX_Boost_LV1
        62722 => 200,    // MAX_Boost_LV2
        62723 => 500,    // MAX_Boost_LV3
        _ => 0,
    };

    public (double Exp, double MaxPct, double MaxFixo, int Hp) Bonus(IEnumerable<UserStore.Item> inventario)
    {
        double exp = 0, maxPct = 0, maxFixo = 0;
        int hp = 0;
        foreach (var i in inventario)
        {
            if (!i.Equipped) continue;

            // O MAX BOOSTER EQUIPA-SE COMO TUDO O RESTO — nao se "usa". Medido: no jogo o
            // botao dele e' o 装備 e o inventario mostra-o com 装備中, e o `0x00BA UseItemReq`
            // nunca aparece no registo de uma sessao inteira.
            //
            // Entra aqui porque a percentagem nao esta' nos dados (ver
            // PercentagemDoMaxBooster): sem esta linha o item equipado somava ZERO e parecia
            // avariado. O EXP Booster ja' funcionava por aqui, porque esse tem o `exp=50` na
            // coluna certa do CSV — foi a comparacao entre os dois que denunciou o buraco.
            maxPct += PercentagemDoMaxBooster(i.CatalogId);

            if (Get(i.CatalogId) is not { } d) continue;
            exp += d.Exp;
            maxPct += d.MaxPct;
            maxFixo += d.MaxFixo;
            hp += d.Hp;
        }
        return (exp, maxPct, maxFixo, hp);
    }
}
