using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// Valida o enquadramento da lista de discos contra bytes reais.
///
/// Vetor: gravacoes/course2_s1.txt, capturada contra 101.32.26.152:23505. O <c>0x0044</c> do
/// login e o <c>0x002A</c> que fecha a primeira etapa levam a MESMA lista — mas o do login
/// poe' o primeiro par no cabecalho.
/// </summary>
public class DefaultItemsTests
{
    /// <summary>0x0044: cabecalho 44 00 | chave 9e | 0x0406 | 26.</summary>
    private const string LoginCabecalhoHex = "44009e06041a00";

    /// <summary>Os primeiros 16 bytes do corpo do 0x0044: a lista a partir do SEGUNDO par.</summary>
    private const string LoginCorpoHex = "05041500040403000804030007040c00";

    /// <summary>0x002A da etapa seguinte: a lista inteira no corpo, o dourado ja' com +1.</summary>
    private const string FechoCorpoHex = "06041b0005041500040403000804030007040c00";

    [Fact]
    public void OPrimeiroParDoLoginVemNoCabecalho()
    {
        var cab = Convert.FromHexString(LoginCabecalhoHex);
        var corpo = new byte[316];
        Convert.FromHexString(LoginCorpoHex).CopyTo(corpo, 0);
        for (int i = 16; i < corpo.Length; i++) corpo[i] = 0xFF;

        var lista = DefaultItems.LerDoLogin(cab, corpo);

        Assert.Equal(
            new[] { (DefaultItems.Discos.Gold, 26), (DefaultItems.Discos.BronzeMax, 21),
                    (DefaultItems.Discos.SilverMax, 3), (DefaultItems.Discos.Bronze, 3),
                    (DefaultItems.Discos.Silver, 12) },
            lista);
    }

    [Fact]
    public void OFechoDeMusicaLevaAListaToda()
    {
        var corpo = new byte[192];
        Convert.FromHexString(FechoCorpoHex).CopyTo(corpo, 0);
        for (int i = 20; i < corpo.Length; i++) corpo[i] = 0xFF;

        var lista = DefaultItems.LerLista(corpo);

        // A mesma lista do login, mas com o dourado tambem no corpo e ja' com +1.
        Assert.Equal(DefaultItems.Discos.Gold, lista[0].Id);
        Assert.Equal(27, lista[0].Qtd);
        Assert.Equal(DefaultItems.Discos.BronzeMax, lista[1].Id);
    }

    [Fact]
    public void EscreverNoLoginDevolveOsMesmosBytes()
    {
        var original = Convert.FromHexString(LoginCabecalhoHex);
        var cab = (byte[])original.Clone();
        var corpo = new byte[316];

        DefaultItems.EscreverNoLogin(cab, corpo, new[]
        {
            (DefaultItems.Discos.Gold, 26), (DefaultItems.Discos.BronzeMax, 21),
            (DefaultItems.Discos.SilverMax, 3), (DefaultItems.Discos.Bronze, 3),
            (DefaultItems.Discos.Silver, 12),
        }, 79);

        Assert.Equal(original, cab);
        Assert.Equal(Convert.FromHexString(LoginCorpoHex), corpo.AsSpan(0, 16).ToArray());
        Assert.Equal(0xFF, corpo[16]);
    }

    [Fact]
    public void OrdenarSegueOTemplateEPoeOsNovosNoFim()
    {
        var contagens = new Dictionary<ushort, int>
        {
            [DefaultItems.Discos.Silver] = 16,
            [DefaultItems.Discos.Gold] = 20,
            [DefaultItems.Discos.Cherry] = 6,
        };
        var lista = DefaultItems.Ordenar(
            new[] { DefaultItems.Discos.Gold, DefaultItems.Discos.BronzeMax, DefaultItems.Discos.Silver },
            contagens);

        // O bronze MAX nao esta' na conta e nao entra; o cherry nao esta' no template e vai
        // para o fim.
        Assert.Equal(new[] { (DefaultItems.Discos.Gold, 20), (DefaultItems.Discos.Silver, 16),
                             (DefaultItems.Discos.Cherry, 6) }, lista);
    }

    /// <summary>
    /// UM DISCO POR DESCOBRIR NAO VAI PARA A LINHA. Presente com 0 o ecra desenha "0";
    /// ausente desenha "?", que e' o que o jogo faz com um disco que nunca se ganhou.
    /// </summary>
    [Fact]
    public void OsZerosNaoVaoParaALinha()
    {
        var contagens = new Dictionary<ushort, int>
        {
            [DefaultItems.Discos.Gold] = 20,
            [DefaultItems.Discos.Silver] = 0,     // por descobrir
            [DefaultItems.Discos.Bronze] = 3,
            [DefaultItems.Discos.Cherry] = 0,     // por descobrir
        };
        var lista = DefaultItems.Ordenar(
            new[] { DefaultItems.Discos.Gold, DefaultItems.Discos.Silver, DefaultItems.Discos.Bronze },
            contagens);

        Assert.Equal(new[] { (DefaultItems.Discos.Gold, 20), (DefaultItems.Discos.Bronze, 3) }, lista);
    }

    /// <summary>Uma conta nova nao tem disco nenhum: a lista sai VAZIA, e o ecra mostra so' "?".</summary>
    [Fact]
    public void UmaContaNovaNaoMandaDiscoNenhum()
    {
        var contagens = DefaultItems.Conhecidos.ToDictionary(id => id, _ => 0);
        var lista = DefaultItems.Ordenar(DefaultItems.Conhecidos, contagens);
        Assert.Empty(lista);
    }

    /// <summary>
    /// E com a lista vazia o `0x0044` tem de sair inteiro em 0xFF — cabecalho incluido, que e'
    /// onde viaja o primeiro par. Sem isto o cliente lia lixo no lugar do primeiro disco.
    /// </summary>
    [Fact]
    public void ComAListaVaziaOLoginSaiTodoAVazio()
    {
        var cab = Convert.FromHexString(LoginCabecalhoHex);
        var corpo = new byte[316];

        DefaultItems.EscreverNoLogin(cab, corpo, Array.Empty<(ushort, int)>(), 79);

        Assert.Equal(DefaultItems.Vazio, BitConverter.ToUInt16(cab, DefaultItems.CabecalhoIdOffset));
        Assert.Equal(0xFF, corpo[0]);
        Assert.Equal(0xFF, corpo[315]);
        Assert.Empty(DefaultItems.LerDoLogin(cab, corpo));
    }

    /// <summary>
    /// Ganhar o primeiro exemplar faz o disco APARECER na lista — e' essa passagem de ausente
    /// a presente que o ecra tem de mostrar como premio.
    /// </summary>
    [Fact]
    public void OPrimeiroExemplarFazODiscoAparecer()
    {
        var contagens = new Dictionary<ushort, int> { [DefaultItems.Discos.Cherry] = 0 };
        Assert.Empty(DefaultItems.Ordenar(new[] { DefaultItems.Discos.Cherry }, contagens));

        contagens[DefaultItems.Discos.Cherry] = 1;
        Assert.Equal(new[] { (DefaultItems.Discos.Cherry, 1) },
                     DefaultItems.Ordenar(new[] { DefaultItems.Discos.Cherry }, contagens));
    }

    /// <summary>
    /// Cada caso e' uma jogada real, com o disco que o ecra de resultado anunciou. Ver a
    /// tabela de amostras em DefaultItems.DiscoDaActuacao.
    /// </summary>
    [Theory]
    [InlineData(99.92, DefaultItems.Discos.GoldMax)]
    [InlineData(99.84, DefaultItems.Discos.GoldMax)]
    [InlineData(99.80, DefaultItems.Discos.SilverMax)]
    [InlineData(99.67, DefaultItems.Discos.SilverMax)]
    [InlineData(99.66, DefaultItems.Discos.SilverMax)]
    [InlineData(99.55, DefaultItems.Discos.BronzeMax)]
    [InlineData(98.88, DefaultItems.Discos.BronzeMax)]
    [InlineData(98.83, DefaultItems.Discos.BronzeMax)]
    [InlineData(98.53, DefaultItems.Discos.BronzeMax)]
    [InlineData(97.84, DefaultItems.Discos.Gold)]
    [InlineData(96.54, DefaultItems.Discos.Gold)]
    [InlineData(96.33, DefaultItems.Discos.Silver)]
    [InlineData(95.87, DefaultItems.Discos.Silver)]
    [InlineData(96.07, DefaultItems.Discos.Silver)]
    [InlineData(95.29, DefaultItems.Discos.Silver)]
    [InlineData(92.97, DefaultItems.Discos.Silver)]
    public void AMedalhaSaiDaPrecisao(double precisao, ushort esperado) =>
        Assert.Equal(esperado, DefaultItems.DiscoDaActuacao(precisao));

    /// <summary>
    /// Os cinco especiais: criterio do FAQ ingles (Q16), id da sonda `discprobe`.
    /// </summary>
    [Theory]
    [InlineData(1.00, DefaultItems.Discos.Sapphire)]
    [InlineData(10.00, DefaultItems.Discos.Ruby)]
    [InlineData(66.60, DefaultItems.Discos.Devil)]
    [InlineData(77.70, DefaultItems.Discos.Rainbow)]
    [InlineData(88.88, DefaultItems.Discos.Dragon)]
    public void OsEspeciaisSaemDaPrecisaoExacta(double precisao, ushort esperado) =>
        Assert.Equal(esperado, DefaultItems.DiscoDaActuacao(precisao));

    /// <summary>
    /// A sonda leu estes numeros no ecra; o id e' `0x0400 + (numero - 90)`. Guardar a
    /// conversao aqui e' o que impede que uma releitura futura os troque outra vez.
    /// </summary>
    [Theory]
    [InlineData(91, DefaultItems.Discos.Ruby)]
    [InlineData(92, DefaultItems.Discos.Sapphire)]
    [InlineData(99, DefaultItems.Discos.Rainbow)]
    [InlineData(100, DefaultItems.Discos.Devil)]
    [InlineData(110, DefaultItems.Discos.Dragon)]
    [InlineData(90, DefaultItems.Discos.Steel)]
    [InlineData(93, DefaultItems.Discos.GoldMax)]
    [InlineData(98, DefaultItems.Discos.Bronze)]
    [InlineData(122, DefaultItems.Discos.Cherry)]
    [InlineData(135, DefaultItems.Discos.Pentavision)]
    public void OsNumerosDaSondaDaoOsIds(int numero, ushort id) =>
        Assert.Equal(id, (ushort)(Tools.DiscProbe.Primeiro + (numero - Tools.DiscProbe.Base)));

    /// <summary>As nove missoes, agrupadas por dificuldade: EZ, NM e HD, tres niveis cada.</summary>
    [Theory]
    [InlineData(0x040B, "mission EZ level 1")]
    [InlineData(0x040D, "mission EZ level 3")]
    [InlineData(0x0410, "mission NM level 3")]
    [InlineData(0x0411, "mission HD level 1")]
    [InlineData(0x0413, "mission HD level 3")]
    public void AsMissoesTemNome(int id, string nome) =>
        Assert.Equal(nome, DefaultItems.Discos.Nome((ushort)id));

    /// <summary>
    /// A sonda `discprobe` fixou a estrutura: 35 discos ao todo, e os cinco especiais NAO sao
    /// contiguos. Este teste guarda o que ela mediu.
    /// </summary>
    [Fact]
    public void AEstruturaDaColeccaoEAQueASondaMediu()
    {
        Assert.Equal(new ushort[] { 0x0401, 0x0402, 0x0409, 0x040A, 0x0414 },
                     DefaultItems.Discos.Especiais.OrderBy(x => x).ToArray());
        Assert.Contains(DefaultItems.Discos.Dragon, DefaultItems.Discos.Especiais);
        Assert.Equal(9, DefaultItems.Discos.UltimaMissao - DefaultItems.Discos.PrimeiraMissao + 1);
        // as missoes nao podem chocar com os especiais
        foreach (ushort e in DefaultItems.Discos.Especiais)
            Assert.False(e >= DefaultItems.Discos.PrimeiraMissao && e <= DefaultItems.Discos.UltimaMissao);
    }

    /// <summary>
    /// A precisao vem em double: a comparacao tem de ser sobre o valor ARREDONDADO a duas
    /// casas, que e' como o ecra a mostra. Sem isso 88,8800001 nao dava disco nenhum.
    /// </summary>
    [Theory]
    [InlineData(88.8800001)]
    [InlineData(88.8849)]
    [InlineData(88.8751)]
    public void OEspecialAceitaOArredondamentoDeDuasCasas(double precisao) =>
        Assert.Equal(DefaultItems.Discos.Dragon, DefaultItems.DiscoDaActuacao(precisao));

    /// <summary>
    /// A RAZAO PELA QUAL OS ESPECIAIS ENTRAM SEM RISCO: caem todos abaixo dos 90,01% onde
    /// comeca o bronze, portanto nao ha' escalao que possam roubar. Este teste protege isso —
    /// se algum dia se mexer num limiar por cima de um valor especial, falha aqui.
    /// </summary>
    [Fact]
    public void NenhumEspecialRoubaUmEscalao()
    {
        foreach (double p in new[] { 1.00, 10.00, 66.60, 77.70, 88.88 })
            Assert.Null(DefaultItems.DiscoDeEscalao(p));
    }

    /// <summary>Fora dos valores exactos nao sai especial nenhum.</summary>
    [Theory]
    [InlineData(88.87)]
    [InlineData(88.89)]
    [InlineData(66.59)]
    [InlineData(1.01)]
    [InlineData(99.92)]
    public void ForaDoValorExactoNaoSaiEspecial(double precisao) =>
        Assert.Null(DefaultItems.DiscoEspecial(precisao));

    /// <summary>
    /// O bloco das frutas, fechado contra a lista de courses do forum: os ids sao contiguos
    /// a partir do cherry e seguem a ordem da folha de sprites.
    /// </summary>
    [Fact]
    public void OBlocoDasFrutasEContiguo()
    {
        ushort[] ordem =
        {
            DefaultItems.Discos.Cherry, DefaultItems.Discos.Banana,
            DefaultItems.Discos.Strawberry, DefaultItems.Discos.Lemon,
            DefaultItems.Discos.Apple, DefaultItems.Discos.Orange,
            DefaultItems.Discos.Kiwi, DefaultItems.Discos.Tomato,
            DefaultItems.Discos.Peach, DefaultItems.Discos.Grape,
            DefaultItems.Discos.Melon, DefaultItems.Discos.Watermelon,
            DefaultItems.Discos.Eternal, DefaultItems.Discos.Pentavision,
        };
        for (int i = 0; i < ordem.Length; i++)
            Assert.Equal((ushort)(0x0420 + i), ordem[i]);

        // E ACABA NO PENTAVISION: a sonda pos numero em 0x042E e o ecra ignorou-o.
        Assert.Equal(14, ordem.Length);
        Assert.Equal(0x042D, ordem[^1]);
    }

    /// <summary>Os ids que o `courses.txt` usa como premio tem de ter nome.</summary>
    [Theory]
    [InlineData(1056, "Cherry")]
    [InlineData(1057, "Banana")]
    [InlineData(1058, "Strawberry")]
    [InlineData(1059, "Lemon")]
    [InlineData(1060, "Apple")]
    [InlineData(1062, "Kiwi")]
    [InlineData(1066, "Melon")]
    [InlineData(1067, "Watermelon")]
    public void OsPremiosDeCourseTemNome(int id, string nome) =>
        Assert.Equal(nome, DefaultItems.Discos.Nome((ushort)id));
}
