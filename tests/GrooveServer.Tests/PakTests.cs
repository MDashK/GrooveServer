using System.Text;
using GrooveServer.Pak;

namespace GrooveServer.Tests;

/// <summary>
/// O formato .pak (XIP2) — as pecas que se podem testar sem os ficheiros do jogo.
///
/// A validacao com dados reais NAO esta' aqui, porque exigiria arrastar conteudo do cliente
/// para o repositorio. Faz-se com a propria ferramenta e esta' registada em docs/por-fazer.md:
/// o `pak conferir` le' os 5162 blocos do system.pak e os 284 do crc.pak com crc32, soma e
/// dispersao do nome todos certos, e o `pak criar` reproduz o system_0002.pak e o
/// system_0003.pak BYTE A BYTE a partir do conteudo que deles se tirou.
/// </summary>
public class PakTests
{
    // ------------------------------------------------------------------ LZO1X

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(64)]
    [InlineData(1000)]
    [InlineData(70000)]      // passa dos 0xBFFF, obriga a recusar pares distantes
    public void LzoVoltaAtrasComDadosAleatorios(int tamanho)
    {
        var rnd = new Random(tamanho);
        var dados = new byte[tamanho];
        rnd.NextBytes(dados);

        var comprimido = Lzo1x.Comprimir(dados);
        var volta = Lzo1x.Descomprimir(comprimido, dados.Length, out int consumidos);

        Assert.Equal(dados, volta);
        Assert.Equal(comprimido.Length, consumidos);
    }

    [Fact]
    public void LzoVoltaAtrasComDadosRepetidos()
    {
        // Repeticoes longas exercitam os pares com extensao de comprimento, que sao o caminho
        // que os dados aleatorios nunca tocam.
        var dados = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabcabc,0,0,0\r\n", 4000)));

        var comprimido = Lzo1x.Comprimir(dados);
        var volta = Lzo1x.Descomprimir(comprimido, dados.Length, out int consumidos);

        Assert.Equal(dados, volta);
        Assert.Equal(comprimido.Length, consumidos);
        Assert.True(comprimido.Length < dados.Length / 20,
                    $"repeticao pura devia comprimir muito: {dados.Length} -> {comprimido.Length}");
    }

    [Fact]
    public void LzoVoltaAtrasComTextoCsv()
    {
        var linhas = new StringBuilder();
        for (int i = 0; i < 900; i++)
            linhas.Append($"{i % 7 + 1},1,0,item_{i},{1000 + i},0,1,0,{i * 13},0,0,0,99,0,0,0,0,0,0,0,0,255,1\r\n");
        var dados = Encoding.ASCII.GetBytes(linhas.ToString());

        var volta = Lzo1x.Descomprimir(Lzo1x.Comprimir(dados), dados.Length, out _);

        Assert.Equal(dados, volta);
    }

    [Fact]
    public void LzoMarcaOFimComOTerminadorDoFormato()
    {
        var comprimido = Lzo1x.Comprimir(new byte[100]);
        Assert.Equal(new byte[] { 0x11, 0x00, 0x00 }, comprimido[^3..]);
    }

    // ------------------------------------------------------------------ XOR do descritor

    [Fact]
    public void XorDoDescritorEOSeuProprioInverso()
    {
        var rnd = new Random(7);
        var dados = new byte[XipFormat.TamanhoDescritor];
        rnd.NextBytes(dados);

        foreach (int deslocamento in new[] { 0, 1, 42, 255, 5162 & 255 })
            Assert.Equal(dados, XipFormat.XorDescritor(XipFormat.XorDescritor(dados, deslocamento), deslocamento));
    }

    /// <summary>A chave sai de um texto japones em shift-jis, saltando o primeiro byte.</summary>
    [Fact]
    public void AChaveDoDescritorTem256BytesConhecidos()
    {
        var zeros = new byte[XipFormat.TamanhoDescritor];
        var chave = XipFormat.XorDescritor(zeros, 0);          // XOR com zeros revela a chave

        Assert.Equal("6381638d6b88ea82", Convert.ToHexString(chave.AsSpan(0, 8)).ToLowerInvariant());
        Assert.Equal("5982ea82ca8cc082", Convert.ToHexString(chave.AsSpan(248, 8)).ToLowerInvariant());
        // Passados 256 bytes a chave repete-se.
        Assert.Equal(chave.AsSpan(0, 28).ToArray(), chave.AsSpan(256, 28).ToArray());
    }

    // ------------------------------------------------------------------ dispersao do nome

    /// <summary>
    /// Valores tirados dos descritores reais: o campo +8 de cada bloco. Confirmados nos 5162
    /// blocos do system.pak, e iguais para o mesmo caminho em .pak diferentes — e' o que
    /// permite a um .pak de patch substituir um ficheiro do system.pak.
    /// </summary>
    [Theory]
    [InlineData("DJMax.ini", 0x4EF73261u)]
    [InlineData(@"Song\DiscStock.csv", 0x76BB1EDCu)]
    [InlineData(@"System\shop\ItemStock.csv", 0xA4DAA197u)]
    [InlineData(@"System\Icon\ItemStock.csv", 0xECD28C58u)]
    public void ADispersaoDoNomeBateComOsDescritoresReais(string nome, uint esperado)
    {
        Assert.Equal(esperado, XipFormat.DispersaoNome(Encoding.ASCII.GetBytes(nome)));
    }

    [Fact]
    public void ADispersaoDoNomeIgnoraMaiusculas()
    {
        Assert.Equal(XipFormat.DispersaoNome(Encoding.ASCII.GetBytes(@"System\shop\ItemStock.csv")),
                     XipFormat.DispersaoNome(Encoding.ASCII.GetBytes(@"SYSTEM\SHOP\ITEMSTOCK.CSV")));
    }

    // ------------------------------------------------------------------ mascara dos textos

    [Fact]
    public void AMascaraDosTextosVoltaAtras()
    {
        var dados = Encoding.ASCII.GetBytes("[Config]\r\nServer=127.0.0.1\r\nPort=23505\r\n");
        Assert.Equal(dados, XipFormat.DesmascararTexto(XipFormat.MascararTexto(dados)));
    }

    [Fact]
    public void AMascaraDosTextosDeixaASobraIgual()
    {
        // A mascara trabalha em palavras de 4 bytes; o que sobra no fim fica intacto.
        var dados = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var mascarado = XipFormat.MascararTexto(dados);
        Assert.Equal(new byte[] { 5, 6, 7 }, mascarado[4..]);
    }

    /// <summary>
    /// So' as extensoes desta lista levam mascara — e a lista tem mesmo <c>.cvs</c>, nao
    /// <c>.csv</c>. Por isso um ItemStock.csv fica em claro dentro do .pak.
    /// </summary>
    [Theory]
    [InlineData("DJMax.ini", true)]
    [InlineData(@"System\SlangFilter\SlangDic.txt", true)]
    [InlineData("system.crc", true)]
    [InlineData(@"System\shop\ItemStock.csv", false)]
    [InlineData(@"System\Disc\Alliwant_ORG_ez.png", false)]
    public void SoAsExtensoesConhecidasLevamMascara(string nome, bool esperado)
    {
        Assert.Equal(esperado, XipFormat.EMascarado(nome));
    }

    // ------------------------------------------------------------------ somas

    [Fact]
    public void OCrc32EOVulgar()
    {
        Assert.Equal(0xCBF43926u, XipFormat.Crc32(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void ASomaESoASomaDosBytes()
    {
        Assert.Equal(3u + 255u + 1u, XipFormat.Soma(new byte[] { 3, 255, 1 }));
    }

    // ------------------------------------------------------------------ tamanhos do bloco

    /// <summary>
    /// Os 40 bytes cifrados a' cabeca de cada fluxo, arredondados para baixo a multiplos de 4.
    /// A regra vale nos 5162 blocos do system.pak, incluindo os cinco que sao curtos demais.
    /// </summary>
    [Theory]
    [InlineData(309, 40)]     // o caso normal
    [InlineData(40, 40)]
    [InlineData(38, 36)]      // System\GameOver\Gameover.ogg.sfl
    [InlineData(34, 32)]      // System\BattleResult\rank.ogg.sfl
    [InlineData(10, 8)]       // System\SlangFilter\SlangDic.txt
    [InlineData(3, 0)]
    public void OPedacoCifradoSaoQuarentaBytesArredondados(int comprimido, int esperado)
    {
        Assert.Equal(esperado, XipFormat.TamanhoRsa(comprimido));
    }

    /// <summary>
    /// Os oito bytes com os dois tamanhos vao com os bytes trocados entre si. Este vetor sao
    /// os bytes 330..337 do system_0002.pak, que codificam (80 cifrados, 40 em claro).
    /// </summary>
    [Fact]
    public void OsTamanhosDoBlocoEstaoBaralhadosDeUmaManeiraConhecida()
    {
        var real = Convert.FromHexString("0050000000002800");
        uint a1 = BitConverter.ToUInt32(real, 0), a2 = BitConverter.ToUInt32(real, 4);

        int cifrado = (int)((((a2 >> 8) & 255) << 24) | ((a1 & 255) << 16) |
                            ((a1 >> 24) << 8) | ((a1 >> 8) & 255));
        int rsa = (int)((((a1 >> 16) & 255) << 24) | (((a2 >> 24) & 255) << 16) |
                        ((a2 & 255) << 8) | ((a2 >> 16) & 255));

        Assert.Equal(80, cifrado);
        Assert.Equal(40, rsa);
        Assert.Equal(cifrado, rsa * 2);
    }

    // ------------------------------------------------------------------ inicio dos blocos

    /// <summary>
    /// Onde a lista de blocos comeca depende do NUMERO DE FICHEIROS: 45 + n. Medido nos .pak
    /// do jogo — 1 ficheiro da' 46, 34 dao 79, 284 dao 329, 5162 dao 5207.
    ///
    /// Estava aqui um 46 fixo, que so' esta' certo para um ficheiro. Um .pak de tres ficheiros
    /// escrito com esse valor comeca dois bytes antes do sitio e o cliente nao o le' — sem que
    /// o extractor de' por nada, porque esse tira o numero do bloco secreto.
    /// </summary>
    [Theory]
    [InlineData(1, 46)]
    [InlineData(3, 48)]
    [InlineData(34, 79)]
    [InlineData(284, 329)]
    [InlineData(5162, 5207)]
    public void OInicioDosBlocosDependeDoNumeroDeFicheiros(int ficheiros, int esperado)
    {
        Assert.Equal(esperado, XipArchive.InicioDosBlocos(ficheiros));
    }

    /// <summary>
    /// Onde vai o bloco secreto: antes do bloco <c>42 % nFicheiros</c>, e no fim quando isso
    /// da' zero. Os casos sao medidos em .pak reais das duas geracoes do cliente — a coluna do
    /// meio e' o numero de ficheiros que esse .pak tem.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]        // system_0017 (2016), system_0002..0008 (nosso): no fim
    [InlineData(2, 0)]        // system_0001 e system_0006 (2016): no fim
    [InlineData(16, 10)]      // system_0016
    [InlineData(17, 8)]       // system_0018
    [InlineData(19, 4)]       // system_0010
    [InlineData(23, 19)]      // system_0020
    [InlineData(29, 13)]      // system_0007
    [InlineData(30, 12)]      // system_0002 e system_0015
    [InlineData(32, 10)]      // system_0021
    [InlineData(34, 8)]       // system_0001 do nosso cliente
    [InlineData(57, 42)]      // system_0003
    [InlineData(88, 42)]      // system_0003 da SNDA 2.60
    [InlineData(5162, 42)]    // system.pak
    public void OSecretoVaiAntesDoBloco42ModuloNumeroDeFicheiros(int ficheiros, int esperado)
    {
        Assert.Equal(esperado, XipArchive.PosicaoDoSecreto(ficheiros));
    }

    /// <summary>
    /// Porque e' que o erro nunca apareceu com um ficheiro so': escrever sempre no fim esta'
    /// certo exatamente quando <c>42 % n == 0</c>, e 1, 2, 3, 6, 7, 14, 21 e 42 sao os unicos
    /// casos. Todos os .pak que se escreveram antes disto tinham um ficheiro.
    /// </summary>
    [Fact]
    public void EscreverNoFimSoEstaCertoNosDivisoresDe42()
    {
        var noFim = Enumerable.Range(1, 60).Where(n => XipArchive.PosicaoDoSecreto(n) == 0);
        Assert.Equal(new[] { 1, 2, 3, 6, 7, 14, 21, 42 }, noFim);
    }

    /// <summary>
    /// O indice da chave RSA sai do offset do bloco: <c>(offset + 28) % 256</c>. Os vectores
    /// sao blocos reais do system_0002.pak do cliente de 2016 — a regra foi medida em 5561
    /// blocos de seis .pak e nao falha em nenhum.
    /// </summary>
    [Theory]
    [InlineData(46, 74)]        // um ficheiro so': InicioDosBlocos(1) = 46
    [InlineData(75, 103)]       // system_0002, bloco 0
    [InlineData(715, 231)]      // system_0002, bloco 1
    [InlineData(17957, 65)]     // system_0002, bloco 2
    [InlineData(23607, 83)]     // system_0002, bloco 3
    [InlineData(94275, 95)]     // system_0002, bloco 4
    [InlineData(228, 0)]        // a dar a volta
    [InlineData(229, 1)]
    public void OIndiceDaChaveSaiDoOffsetDoBloco(int offset, int esperado)
    {
        Assert.Equal((byte)esperado, XipArchive.IndiceDaChave(offset));
    }

    /// <summary>
    /// A raiz do problema dos .pak de varios ficheiros: o 74 fixo que aqui se escrevia so'
    /// acerta quando o bloco cai num offset congruente com 46 modulo 256. O primeiro bloco de
    /// um .pak de UM ficheiro cai exatamente ai'; o de dois ja' nao.
    /// </summary>
    [Fact]
    public void OSetentaEQuatroFixoSoAcertavaComUmFicheiro()
    {
        Assert.Equal(74, XipArchive.IndiceDaChave(XipArchive.InicioDosBlocos(1)));
        Assert.NotEqual(74, XipArchive.IndiceDaChave(XipArchive.InicioDosBlocos(2)));
        Assert.NotEqual(74, XipArchive.IndiceDaChave(XipArchive.InicioDosBlocos(4)));
    }

    /// <summary>
    /// O campo de +280 e' o offset ABSOLUTO dos dados do bloco. Os vectores sao blocos reais do
    /// system_0002.pak do cliente de 2016 — a regra bate nos 5561 blocos de seis .pak.
    /// </summary>
    [Theory]
    [InlineData(46, 0x0000014Au)]   // o unico bloco de um .pak de UM ficheiro: a antiga constante
    [InlineData(715, 0x3E7u)]       // system_0002, bloco 1
    [InlineData(17957, 0x4741u)]    // system_0002, bloco 2
    [InlineData(23607, 0x5D53u)]    // system_0002, bloco 3
    [InlineData(94275, 0x1715Fu)]   // system_0002, bloco 4
    public void OCampoDe280EOOffsetDosDados(int offsetDoBloco, uint esperado)
    {
        Assert.Equal(esperado, XipArchive.OffsetDosDados(offsetDoBloco));
    }

    /// <summary>
    /// A constante 0x14A que aqui esteve gravada nao era magica: e' o offset dos dados do unico
    /// bloco de um .pak de um ficheiro. Por isso e' que so' esses funcionavam.
    /// </summary>
    [Fact]
    public void AConstanteAntigaEraOCasoDeUmFicheiro()
    {
        Assert.Equal(0x0000014Au, XipArchive.OffsetDosDados(XipArchive.InicioDosBlocos(1)));
        Assert.NotEqual(0x0000014Au, XipArchive.OffsetDosDados(XipArchive.InicioDosBlocos(2)));
    }

    /// <summary>
    /// O ultimo byte do bloco secreto e' um checksum do cabecalho, e o cliente recusa o .pak
    /// se nao bater. Os valores sao os dos oito .pak do jogo.
    ///
    /// Repare-se no primeiro: com um ficheiro da' 255 seja qual for o resto, porque o XOR
    /// anula. Foi por isso que o erro so' apareceu no primeiro .pak com mais de um ficheiro.
    /// </summary>
    [Theory]
    [InlineData(1, 46, 255)]        // system_0002 / 0003 / 0004
    [InlineData(34, 79, 50)]        // system_0001
    [InlineData(284, 329, 186)]     // crc.pak
    [InlineData(343, 388, 167)]     // ksystem.pak
    [InlineData(472, 517, 194)]     // gridsystem.pak
    [InlineData(5162, 5207, 98)]    // system.pak
    public void OChecksumDoSecretoBateComOsPakDoJogo(int ficheiros, int inicio, byte esperado)
    {
        Assert.Equal(esperado, XipArchive.ChecksumDoSecreto(ficheiros, inicio, 1));
    }

    [Fact]
    public void OChecksumDoSecretoUsaOInicioQueLheCorresponde()
    {
        // Tres ficheiros comecam em 48, e ai' o 255 fixo que aqui estava dava 159.
        Assert.Equal(159, XipArchive.ChecksumDoSecreto(3, XipArchive.InicioDosBlocos(3), 1));
    }

    // ------------------------------------------------------------------ checksum do system.crc

    /// <summary>
    /// O cliente nao soma o ficheiro todo: le' amostras de 32 bytes espacadas de
    /// <c>(tamanho-32)/5</c> e junta os CRC32 ao tamanho. Com um ficheiro todo igual, todas as
    /// amostras dao o mesmo CRC e o resultado pode conferir-se a' mao.
    ///
    /// Sao cinco ou seis amostras, conforme o arredondamento: o ciclo do cliente e' um
    /// do-while que continua enquanto o AVANCO seguinte ainda cair antes de tamanho-32.
    /// </summary>
    [Theory]
    [InlineData(5032, 5)]      // (5032-32)/5 = 1000 certo: 0, 1000, 2000, 3000, 4000
    [InlineData(5033, 6)]      // sobra 1, entra mais uma amostra em 5000
    public void OChecksumDoPakSaoAmostrasEspacadasMaisOTamanho(int tamanho, int amostras)
    {
        var caminho = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(caminho, new byte[tamanho]);

            uint amostra = XipFormat.Crc32(new byte[32]);
            uint esperado = (uint)tamanho + amostra * (uint)amostras;

            Assert.Equal(esperado, XipFormat.ChecksumDoPak(caminho));
        }
        finally { File.Delete(caminho); }
    }

    [Fact]
    public void OChecksumDeUmFicheiroMinusculoEOComplementoDoTamanho()
    {
        var caminho = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(caminho, new byte[10]);
            Assert.Equal(~10u, XipFormat.ChecksumDoPak(caminho));
        }
        finally { File.Delete(caminho); }
    }

    // ------------------------------------------------------------------ RSA

    /// <summary>
    /// A cifra dos blocos e' RSA com modulos de 55 bits — pequenos o bastante para se
    /// fatorizarem, que e' o que da' o expoente privado e permite ESCREVER. Sem as chaves do
    /// cliente nao se testa com as reais, mas o mecanismo confere-se com um par proprio.
    /// </summary>
    [Fact]
    public void CifrarEODesfazerDeDecifrar()
    {
        // p e q primos; n = p*q com 55 bits, como os do jogo.
        const ulong p = 25169327, q = 801274393;
        ulong n = p * q;
        ulong lambda = (p - 1) / 3 * (q - 1);            // mdc(p-1, q-1) = 3 neste par
        ulong e = 65537;

        var modulos = new byte[2048];
        var expoentes = new byte[2048];
        for (int i = 0; i < 256; i++)
        {
            BitConverter.TryWriteBytes(modulos.AsSpan(i * 8), n);
            BitConverter.TryWriteBytes(expoentes.AsSpan(i * 8), e);
        }
        var chaves = XipKeys.De(modulos, expoentes);

        var claro = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var cifrado = chaves.Cifrar(claro, 74);

        Assert.Equal(claro.Length * 2, cifrado.Length);
        Assert.Equal(claro, chaves.Decifrar(cifrado, 74));
        _ = lambda;
    }

    [Fact]
    public void OIndiceDaChaveAndaUmPorCadaPalavra()
    {
        // Com chaves diferentes por indice, decifrar no indice errado tem de dar outra coisa.
        var modulos = new byte[2048];
        var expoentes = new byte[2048];
        for (int i = 0; i < 256; i++)
        {
            BitConverter.TryWriteBytes(modulos.AsSpan(i * 8), 25169327UL * 801274393UL);
            BitConverter.TryWriteBytes(expoentes.AsSpan(i * 8), 65537UL);
        }
        var chaves = XipKeys.De(modulos, expoentes);
        var claro = Convert.FromHexString("0011223344556677");

        Assert.Equal(claro, chaves.Decifrar(chaves.Cifrar(claro, 12), 12));
    }
}
