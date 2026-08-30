using System.Net;
using GrooveServer.Net;

// GrooveServer — emulador de servidor para o cliente DJMAX Online.
//
// A cifra de sessao (TEA + MT19937) esta' validada contra trafego real capturado;
// ver tests/GrooveServer.Tests/CipherValidationTests.cs.
//
// Uso:  dotnet run --project src/GrooveServer [porta]

// Modo de analise: reproduz e decifra uma sessao capturada.
if (args.Length >= 2 && args[0] == "analyze")
{
    GrooveServer.Tools.StreamAnalyzer.Run(args[1]);
    return;
}

// Modo de colheita: constroi a tabela de tamanhos a partir de segmentos isolados.
if (args.Length >= 2 && args[0] == "sizes")
{
    GrooveServer.Tools.SizeHarvester.Run(args.Skip(1).ToArray());
    return;
}

// Modo automatico: descobre a tabela S2C inteira pelas fronteiras que a cifra denuncia.
if (args.Length >= 2 && args[0] == "autoframe")
{
    GrooveServer.Tools.AutoFramer.Diagnose = Environment.GetEnvironmentVariable("DIAG") == "1";
    GrooveServer.Tools.AutoFramer.Run("MDashK", args.Skip(1).ToArray());
    return;
}

// Modo pak: le' e ESCREVE os .pak do jogo. Ver Tools/PakTool.cs para a receita.
if (args.Length >= 1 && args[0] == "pak")
{
    GrooveServer.Tools.PakTool.Run(args.Skip(1).ToArray());
    return;
}

// Modo dump: decifra e imprime cada pacote, marcando IPs embutidos.
if (args.Length >= 2 && args[0] == "dump")
{
    GrooveServer.Tools.PacketDump.Run(args[1]);
    return;
}

// Modo musicas: extrai a lista de musicas de uma mensagem e cruza com os .pak locais.
if (args.Length >= 2 && args[0] == "songs")
{
    GrooveServer.Tools.PacketDump.Songs(args[1], 0x007A,
        args.Length > 2 ? args[2] : @"E:\Groove\DJMAX-Full 19012000\FILES");
    return;
}

// Modo cliente: decifra os pacotes do cliente e compara ocorrencias do mesmo tipo.
if (args.Length >= 2 && args[0] == "client")
{
    GrooveServer.Tools.ClientDump.Run(args[1],
        args.Length > 2 ? Convert.ToUInt16(args[2], 16) : (ushort)0x0076);
    return;
}

// Modo bloco: analisa o bloco opaco do GameInfoInf.
if (args.Length >= 2 && args[0] == "block")
{
    GrooveServer.Tools.BlockAnalyzer.Run(args[1]);
    return;
}

// Modo comparacao: compara varias ocorrencias da mesma mensagem do servidor.
if (args.Length >= 2 && args[0] == "compare")
{
    GrooveServer.Tools.BlockCompare.Run(args[1],
        args.Length > 2 ? Convert.ToUInt16(args[2], 16) : (ushort)0x007A);
    return;
}

// Modo colheita: extrai de cada captura o grupo de arranque de cada musica.
//
// O CANAL E' OBRIGATORIO NA PRATICA, ainda que tenha valor por omissao. O harvest escreve
// `song_<musica>_d<dificuldade>.bin` e escreve POR CIMA do que la' estiver; como o par
// (musica, dificuldade) e' o mesmo em 5K e em 7K, colher uma captura de 7K para a pasta do
// 5K destruiria a biblioteca de 5K sem dar sinal. Uso: `harvest [5k|7k] <captura>...`.
if (args.Length >= 2 && args[0] == "harvest")
{
    string canal = GrooveServer.Config.Canal5K;
    var capturas = args.Skip(1).ToArray();
    if (capturas.Length >= 2 &&
        (capturas[0] == GrooveServer.Config.Canal5K || capturas[0] == GrooveServer.Config.Canal7K))
    {
        canal = capturas[0];
        capturas = capturas.Skip(1).ToArray();
    }
    var dir = GrooveServer.Config.MusicasDoCanal(canal);
    Directory.CreateDirectory(dir);
    Console.WriteLine($"canal {canal} -> {dir}\n");
    int total = 0;
    foreach (var cap in capturas)
    {
        Console.WriteLine($"--- {Path.GetFileName(cap)}");
        var m = GrooveServer.Net.ResponseMap.Load(cap);
        total += GrooveServer.Net.SongLibrary.Harvest(cap, m, dir);
    }
    var lib = new GrooveServer.Net.SongLibrary(dir);
    Console.WriteLine($"\nbiblioteca {canal}: {lib.Count} charts ({total} novos) em {dir}");
    foreach (var k in lib.Keys) Console.WriteLine($"  {k}");
    return;
}

// Modo biblioteca: mostra o que varia entre os grupos de arranque guardados.
if (args.Length >= 1 && args[0] == "libdump")
{
    GrooveServer.Tools.LibraryDump.Run(GrooveServer.Config.Musicas);
    return;
}

// Modo verificacao: confere os blocos guardados contra a captura de origem.
//
// O CANAL E' OPCIONAL MAS QUASE SEMPRE PRECISO: sem ele confere-se contra a biblioteca de 5K,
// e uma captura de 7K dava 117 falhas que nao eram falhas nenhumas — era a pasta errada.
// Uso: `libverify <captura> [5k|7k]`.
if (args.Length >= 2 && args[0] == "libverify")
{
    var pasta = args.Length > 2
        ? GrooveServer.Config.MusicasDoCanal(args[2].ToLowerInvariant())
        : GrooveServer.Config.Musicas;
    GrooveServer.Tools.LibraryVerify.Run(args[1], pasta);
    return;
}

// Modo dessincronia: localiza onde a decifra do cliente descarrila.
if (args.Length >= 2 && args[0] == "desync")
{
    GrooveServer.Tools.DesyncFinder.Run(args[1]);
    return;
}

// Modo perfil: mapeia os campos do jogador com valores conhecidos do ecra.
if (args.Length >= 2 && args[0] == "profile")
{
    GrooveServer.Tools.ProfileDecoder.Run(args[1],
        ("preco", 9000), ("MAX depois", 5895), ("MAX antes", 14895), ("HP bonus", 15), ("limite nivel", 6), ("xp bonus %", 10), ("id conta", 101382), ("id item", 20261830));
    return;
}

// Modo despejo do processo: le' a imagem do cliente ja' descomprimida em memoria.
if (args.Length >= 2 && args[0] == "procdump")
{
    GrooveServer.Tools.ProcessDump.Run(args[1], args.Length > 2 ? args[2] : "djmax_dump.bin");
    return;
}

// Sonda dos discos: numero distinto em cada id, para o ecra de coleccao os identificar.
//   discprobe <conta> [repor]
if (args.Length >= 2 && args[0] == "discprobe")
{
    GrooveServer.Tools.DiscProbe.Run(GrooveServer.Config.Caminho("users.json"), args[1],
                                     args.Length > 2 && args[2] == "repor");
    return;
}

// Modo mapa: percorre um std::map do cliente vivo (os nos estao no heap, fora do despejo).
//   mapwalk DJMax.client 0x0076CAA4 [bytesPorNo] [saida.csv]
if (args.Length >= 3 && args[0] == "mapwalk")
{
    uint va = Convert.ToUInt32(args[2].Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
    int bytes = args.Length > 3 && int.TryParse(args[3], out var b) ? b : 0xC0;
    string? csv = args.LastOrDefault(a => a.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
    GrooveServer.Tools.MapWalk.Run(args[1], va, bytes, csv);
    return;
}

// Modo varrimento: procura valores no espaco de enderecos todo do cliente.
if (args.Length >= 3 && args[0] == "procscan")
{
    GrooveServer.Tools.ProcessDump.Scan(args[1], args.Skip(2).Select(uint.Parse).ToArray());
    return;
}

// Modo verificacao de segmentos: confere o enquadramento contra os limites TCP.
if (args.Length >= 2 && args[0] == "segcheck")
{
    GrooveServer.Tools.SegmentCheck.Run(args[1], args.Length > 2 ? args[2] : "S2C");
    return;
}

// Modo credenciais: revela utilizador e password de uma captura.
if (args.Length >= 2 && args[0] == "creds")
{
    GrooveServer.Tools.CredentialTest.Run(args[1]);
    return;
}

// Modo linha temporal: mostra a troca de mensagens pela ordem real.
if (args.Length >= 2 && args[0] == "timeline")
{
    GrooveServer.Tools.Timeline.Skip = args.Length > 4 && int.TryParse(args[4], out var sk) ? sk : 0;
    GrooveServer.Tools.Timeline.BodyBytes = args.Length > 5 && int.TryParse(args[5], out var bb) ? bb : 16;
    GrooveServer.Tools.Timeline.Run(args[1], args.Length > 2 ? args[2] : "",
        args.Length > 3 && int.TryParse(args[3], out var ctx) ? ctx : 12);
    return;
}

// Modo comparar respostas: o que muda entre duas ocorrencias do mesmo pedido.
if (args.Length >= 4 && args[0] == "cmpresp")
{
    GrooveServer.Tools.ResponseCompare.Run(args[1], Convert.ToUInt16(args[2], 16),
        int.Parse(args[3]), int.Parse(args[4]));
    return;
}

// Modo comparar capturas: a mesma mensagem em duas sessoes diferentes.
if (args.Length >= 4 && args[0] == "xcmp")
{
    GrooveServer.Tools.CrossCompare.Run(args[1], args[2], Convert.ToUInt16(args[3], 16));
    return;
}

// Modo estrutura do inventario: confirma onde comeca cada seccao.
if (args.Length >= 2 && args[0] == "invlayout")
{
    GrooveServer.Tools.InventoryLayout.Run(args.Skip(1).ToArray());
    return;
}

// Modo sonda: localiza a fronteira do primeiro pacote observando a decifra.
if (args.Length >= 2 && args[0] == "probe")
{
    GrooveServer.Tools.CipherProbe.Run(args[1]);
    return;
}

// Modo rajada: decompoe as rajadas com minimos + decifra + oraculo do nome.
if (args.Length >= 2 && args[0] == "burst")
{
    GrooveServer.Tools.BurstSolver.Run(args[1], args.Length > 2 ? args[2] : "MDashK");
    return;
}

// Modo propagacao: deduz tamanhos por subtracao dentro de cada troco.
if (args.Length >= 3 && args[0] == "propagate")
{
    GrooveServer.Tools.SizePropagator.Run(args[1], args.Skip(2).ToArray());
    return;
}

// Modo tabela: resolve os tamanhos por msgid como restricao global.
if (args.Length >= 3 && args[0] == "table")
{
    GrooveServer.Tools.SizeTableSolver.Run(args[1], args.Skip(2).ToArray());
    return;
}

// Modo de enquadramento: deduz as fronteiras dos pacotes so' pelos cabecalhos.
if (args.Length >= 2 && args[0] == "framing")
{
    GrooveServer.Tools.FramingSolver.Run(args[1], args.Length > 2 ? args[2] : "S2C");
    return;
}

// Cliente sintetico: reproduz pacotes reais contra o servidor e compara as respostas.
if (args.Length >= 2 && args[0] == "testclient")
{
    await GrooveServer.Tools.SyntheticClient.RunAsync(
        args[1],
        args.Length > 2 ? args[2] : "127.0.0.1",
        args.Length > 3 && int.TryParse(args[3], out var tp) ? tp : 23505);
    return;
}

// Modo replay: conduz o cliente reproduzindo uma sessao gravada.
// O cliente liga-se duas vezes � autentica, desliga, volta a ligar para jogar � por
// isso ha' um guiao para cada ligacao.
var rest = args.ToList();

// -v: o arranque detalhado. Sem isto sai um resumo de tres linhas — a lista dos charts, dos
// courses e o mapa de respostas de cada gravacao sao centenas de linhas que empurram o log da
// sessao para fora do ecra.
if (rest.Remove("-v") | rest.Remove("--verbose")) GrooveServer.Config.Verboso = true;

// --courses N: quantos courses o CLIENTE conhece. Um cliente mais antigo tem menos
// entradas no CourseSection.ini dele, e receber um indice que la' nao existe CRASHA-O
// ao entrar no modo course. Ver Config.LimiteDeCourses, que tem os numeros medidos.
int ciIdx = rest.IndexOf("--courses");
if (ciIdx >= 0 && ciIdx + 1 < rest.Count && int.TryParse(rest[ciIdx + 1], out var limiteCursos))
{
    GrooveServer.Config.LimiteDeCourses = limiteCursos;
    rest.RemoveRange(ciIdx, 2);
}

// O endereco dos canais vai dentro do payload cifrado (ChannelInfoInf); sem o
// reescrever, o cliente escolhe um modo e liga-se ao servidor original.
(System.Net.IPAddress From, System.Net.IPAddress To)? rewrite =
    (System.Net.IPAddress.Parse(GrooveServer.Config.ServidorOriginal),
     System.Net.IPAddress.Parse(GrooveServer.Config.ServidorLocal));
int rwIdx = rest.IndexOf("--rewrite-ip");
if (rwIdx >= 0 && rwIdx + 2 < rest.Count)
{
    rewrite = (System.Net.IPAddress.Parse(rest[rwIdx + 1]), System.Net.IPAddress.Parse(rest[rwIdx + 2]));
    rest.RemoveRange(rwIdx, 3);
}

ReplaySource? Take(string flag)
{
    int i = rest.IndexOf(flag);
    if (i < 0 || i + 1 >= rest.Count) return null;
    var src = ReplaySource.Load(rest[i + 1], rewrite);
    Console.WriteLine($"{flag}: {Path.GetFileName(rest[i + 1])} � {src.Steps.Count} passos, {src.TotalBytes} bytes");
    rest.RemoveRange(i, 2);
    return src;
}

var authScript = Take("--replay-auth");
var gameScript = Take("--replay-game") ?? Take("--replay");

// Modo responsivo: em vez de repetir um guiao por ordem, responde a cada pedido com o
// que o servidor original respondeu a esse tipo de pedido. E' o que permite carregar em
// ESC ou escolher outra musica sem pendurar o cliente.
ResponseMap? TakeMap(string flag, string? porOmissao)
{
    int i = rest.IndexOf(flag);
    string? caminho = i >= 0 && i + 1 < rest.Count ? rest[i + 1] : null;
    if (caminho is not null) rest.RemoveRange(i, 2);
    caminho ??= porOmissao is null ? null : GrooveServer.Config.Gravacao(porOmissao);
    if (caminho is null || !File.Exists(caminho)) return null;
    return ResponseMap.Load(caminho, rewrite?.From, rewrite?.To);
}
// Experimental: reescrever o id da musica no GameInfoInf. Crasha o cliente � ver
// ResponsiveSession.EchoSelectedSong.
if (rest.Remove("--echo-song")) GrooveServer.Net.ResponsiveSession.EchoSelectedSong = true;

// Biblioteca de blocos por musica: sem ela o servidor so' sabe servir as musicas que
// estavam nas gravacoes.
var songLibrary = new SongLibrary(GrooveServer.Config.Musicas);
if (songLibrary.Count > 0 && args.Length > 0 && args[0] == "courseinfo")
    Console.WriteLine($"library: {songLibrary.Count} charts");
else if (songLibrary.Count > 0 && GrooveServer.Config.Verboso)
    Console.WriteLine($"library: {songLibrary.Count} charts ({string.Join(", ", songLibrary.Keys)})");

// Etapas de um course gravado: musica e dificuldade de cada uma.
if (args.Length >= 2 && args[0] == "courseinfo")
{
    GrooveServer.Tools.CourseInfo.Run(args[1], songLibrary);
    return;
}
if (args.Length >= 2 && args[0] == "blocos")
{
    GrooveServer.Tools.CourseInfo.BlocosDaGravacao(args[1]);
    return;
}
// Modo limpeza: tira as credenciais de uma gravacao, para ela poder ser publicada.
if (args.Length >= 2 && args[0] == "limpar")
{
    foreach (var f in args.Skip(1).Where(x => x != "--escrever"))
        GrooveServer.Tools.Sanitize.Run(f, args.Contains("--escrever"));
    return;
}
if (args.Length >= 2 && args[0] == "xp")
{
    foreach (var f in args.Skip(1)) GrooveServer.Tools.RankHeaders.Xp(f);
    return;
}
if (args.Length >= 2 && args[0] == "perfil")
{
    GrooveServer.Tools.RankHeaders.Perfil(args[1]);
    return;
}
if (args.Length >= 3 && args[0] == "msg")
{
    GrooveServer.Tools.RankHeaders.Mensagens(args[1], Convert.ToUInt16(args[2], 16),
                                             args.Length >= 4 ? int.Parse(args[3]) : 4);
    return;
}
if (args.Length >= 3 && args[0] == "acha")
{
    GrooveServer.Tools.RankHeaders.Procura(args[1], ushort.Parse(args[2]));
    return;
}
if (args.Length >= 2 && args[0] == "rankhdr")
{
    GrooveServer.Tools.RankHeaders.Run(args[1], args.Length >= 3 ? int.Parse(args[2]) : -1);
    return;
}
if (args.Length >= 2 && args[0] == "coursesgrav")
{
    GrooveServer.Tools.CourseInfo.CoursesGravados(args.Skip(1).ToArray());
    return;
}
if (args.Length >= 2 && args[0] == "prefixcmp")
{
    GrooveServer.Tools.CourseInfo.PrefixoLadoALado(args[1], songLibrary, args.Length >= 3 ? int.Parse(args[2]) : 1);
    return;
}
if (args.Length >= 1 && args[0] == "prefixlib")
{
    GrooveServer.Tools.CourseInfo.PrefixosDaBiblioteca(songLibrary, args.Length >= 2 ? int.Parse(args[1]) : 12);
    return;
}
if (args.Length >= 2 && args[0] == "arrvslib")
{
    GrooveServer.Tools.CourseInfo.ArranqueVsBiblioteca(args[1], songLibrary);
    return;
}
if (args.Length >= 2 && args[0] == "fechos")
{
    GrooveServer.Tools.CourseInfo.Fechos(args[1], args.Length >= 3 ? Convert.ToUInt16(args[2], 16) : (ushort)0,
        args.Length >= 4 ? Convert.ToUInt16(args[3], 16) : (ushort)0x002A);
    return;
}
if (args.Length >= 3 && args[0] == "blococmp")
{
    GrooveServer.Tools.CourseInfo.Comparar(args[1], args[2]);
    return;
}

// Que musicas compoe cada course. Sem esta tabela, o course mode so' toca o course que
// esta' gravado, seja qual for o que o jogador escolhe.
// Catalogo das musicas: so' para o log poder dizer o nome em vez do numero.
var catalogo = GrooveServer.Net.SongCatalog.Load(GrooveServer.Config.Catalogo);
GrooveServer.Net.ResponsiveSession.Catalogo = catalogo;

var courseTable = GrooveServer.Net.CourseTable.Load(GrooveServer.Config.Courses);
if (courseTable.Count > 0 && GrooveServer.Config.Verboso)
    Console.WriteLine($"courses: {courseTable.Count} defined - " +
                      string.Join(", ", courseTable.Todos.Select(c => $"{c.Indice}:{c.Nome}")));

// OS COURSES ESCONDIDOS DIZEM-SE SEMPRE, com ou sem `-v`. Sao os que nao chegam a' lista de
// escolha do jogador, e explicar porque' vale mais do que o silencio.
if (courseTable.Count > 0) courseTable.Verificar(songLibrary, GrooveServer.Config.Canal5K);
GrooveServer.Net.ResponsiveSession.Courses = courseTable;

var itemTable = GrooveServer.Net.ItemTable.Load(GrooveServer.Config.Itens);
GrooveServer.Net.ResponsiveSession.Itens = itemTable;

// A traducao de ids para o cliente SNDA de 2007. So' e' usada se um cliente dessa versao se
// ligar — o nosso nao passa por ela.
var mapaSnda = GrooveServer.Net.SongIdMap.Load(GrooveServer.Config.MusicasSnda);
GrooveServer.Net.ResponsiveSession.MusicasSnda = mapaSnda;

// Os charts contam-se por canal: sao bibliotecas separadas porque o par (musica, dificuldade)
// e' o mesmo nos dois e o chart nao. Ver Config.MusicasDoCanal.
int charts7K = Directory.Exists(GrooveServer.Config.MusicasDoCanal(GrooveServer.Config.Canal7K))
    ? Directory.GetFiles(GrooveServer.Config.MusicasDoCanal(GrooveServer.Config.Canal7K), "song_*.bin").Length
    : 0;

// O INVENTARIO DO QUE FOI CARREGADO SO' SAI COM `-v`. No arranque normal nao diz nada a quem
// so' quer por o servidor de pe'; quem esta' a mexer nos dados e' que quer os numeros.
if (GrooveServer.Config.Verboso)
    Console.WriteLine($"data: {songLibrary.Count} charts for 5K" +
                      (charts7K > 0 ? $" and {charts7K} for 7K" : "") +
                      $", {catalogo.Count} songs in the catalogue, " +
                      $"{courseTable.Count} courses, {itemTable.Count} items" +
                      (mapaSnda.Count > 0 ? $", {mapaSnda.Count} songs mapped to SNDA 2007" : ""));

// Contas. O ficheiro carrega-se SEMPRE: e' o <c>AuthenticateInACCReq</c> que diz quem se
// esta' a ligar (Protocol/Credentials.cs), e sem o UserStore carregado a sessao nao teria
// onde procurar a conta.
//
// O `--user` deixou de ser preciso. Continua a aceitar-se, e serve para dois casos: dizer
// qual a conta ativa antes de alguem se ligar, e cobrir uma sessao onde as credenciais nao
// cheguem. Assim que o cliente faz login, o nome que la' escreveu e' que manda.
// Interruptor de diagnostico: desliga TODAS as reescritas de perfil, inventario e avatar,
// servindo as mensagens gravadas tal e qual. Se um problema desaparecer com isto, a causa
// esta' numa das reescritas � e' um teste de isolamento, nao uma opcao de uso normal.
if (rest.Remove("--sem-xp"))
{
    GrooveServer.Net.ResponsiveSession.SemXp = true;
    Console.WriteLine("DIAGNOSTICO: experiencia nao reescrita");
}
if (rest.Remove("--sem-max"))
{
    GrooveServer.Net.ResponsiveSession.SemMax = true;
    Console.WriteLine("DIAGNOSTICO: MAX nao reescrito");
}
if (rest.Remove("--sem-perfil"))
{
    GrooveServer.Net.ResponsiveSession.SemPerfil = true;
    Console.WriteLine("DIAGNOSTICO: reescritas de perfil desligadas");
}
int iLogin = rest.IndexOf("--login-de");
if (iLogin >= 0 && iLogin + 1 < rest.Count)
{
    GrooveServer.Net.ResponsiveSession.LoginDe = rest[iLogin + 1];
    Console.WriteLine($"DIAGNOSTICO: rajada de login servida por {rest[iLogin + 1]}");
    rest.RemoveRange(iLogin, 2);
}

int iCa = rest.IndexOf("--connectack-de");
if (iCa >= 0 && iCa + 1 < rest.Count)
{
    GrooveServer.Net.ResponsiveSession.ConnectAckDe = rest[iCa + 1];
    rest.RemoveRange(iCa, 2);
    Console.WriteLine($"DIAGNOSTICO: ConnectAck servido por {GrooveServer.Net.ResponsiveSession.ConnectAckDe}");
}
if (rest.Remove("--sem-resultados"))
{
    GrooveServer.Net.ResponsiveSession.SemResultados = true;
    Console.WriteLine("DIAGNOSTICO: 0x0070 (resultados) sem reescrita");
}
if (rest.Remove("--sem-perfil-fecho"))
{
    GrooveServer.Net.ResponsiveSession.SemPerfilFecho = true;
    Console.WriteLine("DIAGNOSTICO: 0x0025 (perfil no fecho) sem reescrita");
}
if (rest.Remove("--sem-fecho"))
{
    GrooveServer.Net.ResponsiveSession.SemFecho = true;
    Console.WriteLine("DIAGNOSTICO: mensagens de fecho de musica sem reescrita");
}
bool semBiblioteca = rest.Remove("--sem-biblioteca");
if (semBiblioteca)
    Console.WriteLine("DIAGNOSTICO: biblioteca de musicas desligada � vai tocar a musica da gravacao");
if (rest.Remove("--inv-gravado")) GrooveServer.Net.ResponsiveSession.InventarioGravado = true;
if (rest.Remove("--course-gravado")) GrooveServer.Net.ResponsiveSession.CourseGravado = true;
if (rest.Remove("--salas-gravadas")) GrooveServer.Net.ResponsiveSession.SalasGravadas = true;
if (rest.Remove("--sem-itens-conta")) GrooveServer.Net.ResponsiveSession.ItensDaConta = false;
if (rest.Remove("--replica"))
{
    GrooveServer.Net.ResponsiveSession.Replica = true;
    GrooveServer.Net.ResponsiveSession.SemPerfil = true;
    Console.WriteLine("DIAGNOSTICO: modo replica � as gravacoes saem sem qualquer reescrita");
    Console.WriteLine("             (a conta que aparece no jogo e' a de quem gravou)");
}

int iVel = rest.IndexOf("--velocidade");
if (iVel >= 0 && iVel + 1 < rest.Count && byte.TryParse(rest[iVel + 1], out var vel))
{
    GrooveServer.Net.ResponsiveSession.Velocidade = vel;
    rest.RemoveRange(iVel, 2);
    Console.WriteLine($"DIAGNOSTICO: +2 do bloco forcado a {vel} (velocidade x{vel / 10.0:0.0}?)");
}

var store = new GrooveServer.Net.UserStore(GrooveServer.Config.Utilizadores);
GrooveServer.Net.ResponsiveSession.Store = store;

int uIdx = rest.IndexOf("--user");
string? userName = uIdx >= 0 && uIdx + 1 < rest.Count ? rest[uIdx + 1] : null;
if (uIdx >= 0) rest.RemoveRange(uIdx, userName is null ? 1 : 2);
rest.Remove("--live-profile");   // ja' nao faz nada; aceite para nao partir comandos antigos

{
    var conta = store.GetOrCreate(userName ?? store.Accounts.FirstOrDefault()?.Name ?? "MDashK");
    GrooveServer.Net.ResponsiveSession.Profile = store.Bind(conta);

    // O nome de quem gravou esta' escrito dentro das mensagens; trocar pelo da conta.
    GrooveServer.Net.ResponsiveSession.Names =
        new GrooveServer.Protocol.NameRewriter("MDashK", conta.Name);

    GrooveServer.Net.ResponsiveSession.Account = conta;

    // ISTO NAO E' A CONTA DA SESSAO, e anuncia-lo como se fosse enganava. Desde que o
    // AuthenticateInACCReq passou a ler-se (Protocol/Credentials.cs) quem manda e' o nome que
    // o jogador escreve no ecra de login; o que se liga aqui e' so' o que vale ATE' esse login
    // chegar. Dizer "conta inicial: MDashK - nivel 13, 998680 MAX" dava a entender que o
    // servidor estava presa a ela, que era verdade em tempos e ha' muito deixou de ser.
    if (userName is not null)
        Console.WriteLine($"--user {conta.Name}: default account until the client logs in");
    else if (GrooveServer.Config.Verboso && store.Accounts.Count > 0)
        Console.WriteLine($"accounts ({store.Accounts.Count}): " +
                          $"{string.Join(", ", store.Accounts.Select(a => a.Name))}" +
                          "   (the session's comes from the client login)");
    else if (GrooveServer.Config.Verboso)
        Console.WriteLine("no accounts on file; the first one is created at the first login");
}

// Gravacoes suplementares: cobrem partes do jogo que a principal nao viu (a loja, por
// exemplo). Consultadas so' quando a principal nunca viu o pedido.
var suplementares = new List<string>();
while (true)
{
    int i = rest.IndexOf("--serve-extra");
    if (i < 0 || i + 1 >= rest.Count) break;
    suplementares.Add(rest[i + 1]);
    rest.RemoveRange(i, 2);
}
// Sem nenhuma indicada, usa-se o conjunto que cobre o jogo todo.
if (suplementares.Count == 0)
    suplementares.AddRange(GrooveServer.Config.Suplementares.Select(GrooveServer.Config.Gravacao));

foreach (var caminho in suplementares.Where(File.Exists))
{
    GrooveServer.Net.ResponsiveSession.Extras.Add(
        GrooveServer.Net.ResponseMap.Load(caminho, rewrite?.From, rewrite?.To));
    if (GrooveServer.Config.Verboso)
        Console.WriteLine($"supplementary recording: {Path.GetFileName(caminho)}");
}
if (GrooveServer.Config.Verboso)
    Console.WriteLine($"recordings: {suplementares.Count(File.Exists)} supplementary");

GrooveServer.Net.NomesDeItens.Carregar(GrooveServer.Config.NomesDeItens);
foreach (var falta in suplementares.Where(c => !File.Exists(c)))
    Console.WriteLine($"WARNING: supplementary recording missing: {falta}");

var authMap = TakeMap("--serve-auth", GrooveServer.Config.Auth);
var gameMap = TakeMap("--serve-game", GrooveServer.Config.Jogo);

int port = rest.Count > 0 && int.TryParse(rest[0], out var p) ? p : GrooveServer.Config.Porta;

Console.WriteLine("=== GrooveServer ===");
if (GrooveServer.Config.Verboso)
{
    Console.WriteLine(
        authMap is not null || gameMap is not null ? "Responsive mode (answers every request)."
        : authScript is not null || gameScript is not null ? "Replay mode (repeats a script in order)."
        : "Native mode (still incomplete).");
    Console.WriteLine();
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Apontar o cliente para aqui escrevendo-lhe o endereco na memoria. So' com `--redirect`:
// hoje o caminho normal e' o `DJMax.ini` dentro de um .pak de patch (ver `pak` e A21), que
// faz o mesmo sem tocar noutro processo. A injeccao serve para alternar entre este servidor
// e o original sem trocar ficheiros — enquanto isto corre o jogo vem para ca', fechado volta
// ao original, que e' o interruptor preciso para continuar a fazer capturas.
rest.Remove("--sem-redirect");   // ja' nao faz nada; aceite para nao partir comandos antigos
if (rest.Remove("--redirect") || GrooveServer.Config.RedirecionarCliente)
{
    _ = GrooveServer.Net.ClientRedirect.VigiarAsync(Console.WriteLine, cts.Token);
    Console.WriteLine("--redirect: watching for the client to write the address into its memory");
}

var server = new GameServer(new IPEndPoint(IPAddress.Any, port),
                            authScript: authScript ?? gameScript,
                            gameScript: gameScript,
                            authMap: authMap ?? gameMap,
                            gameMap: gameMap,
                            advertise: rewrite?.To ?? IPAddress.Loopback)
{
    Rotulo = "5KEY",
    Canal = GrooveServer.Config.Canal5K,
    // Sem biblioteca, o arranque serve o GameInfoInf tal como foi gravado: toca sempre a
    // musica de quem gravou, seja qual for a escolhida. Nao serve para jogar � serve para
    // separar "o bloco que eu construo esta' mal" de "o resto do arranque esta' mal", que
    // e' a unica peca do arranque que nunca veio de uma captura.
    Songs = semBiblioteca ? null : songLibrary,
};

// ---------------------------------------------------------------- canal de 7 teclas
//
// O canal escolhe-se PELA PORTA. O `ChannelInfoInf` da autenticacao ja' anunciava os dois —
// LIGHT "[5KEY] Classic" na 23505 e MANIA "[7KEY] Classic" na 23705 — e a reescrita de
// endereco ja' trazia os dois para ca'. So' faltava haver alguem a atender na segunda: quem
// escolhesse 7 teclas batia numa porta fechada.
//
// A ligacao ao canal e' uma sessao completa por si (tem ConnectAck, login, lobby e salas
// proprios), por isso o unico que muda em relacao ao 5K sao as duas fontes: a gravacao e a
// biblioteca de charts. O resto do codigo da sessao nao precisa de saber em que canal esta'.
var jogo7K = GrooveServer.Config.Gravacao(GrooveServer.Config.Jogo7K);
var biblioteca7K = new SongLibrary(
    GrooveServer.Config.MusicasDoCanal(GrooveServer.Config.Canal7K));

GameServer? servidor7K = null;
if (!File.Exists(jogo7K))
    Console.WriteLine($"7KEY: off — missing recording {GrooveServer.Config.Jogo7K}");
else if (biblioteca7K.Count == 0)
    Console.WriteLine("7KEY: off — the songs\\7k library is empty");
else
{
    var mapa7K = GrooveServer.Net.ResponseMap.Load(jogo7K, rewrite?.From, rewrite?.To);
    servidor7K = new GameServer(new IPEndPoint(IPAddress.Any, GrooveServer.Config.Porta7K),
                                authMap: mapa7K,
                                gameMap: mapa7K,
                                advertise: rewrite?.To ?? IPAddress.Loopback)
    {
        Rotulo = "7KEY",
        Canal = GrooveServer.Config.Canal7K,
        Songs = semBiblioteca ? null : biblioteca7K,
    };
    if (GrooveServer.Config.Verboso)
        Console.WriteLine($"7KEY: MANIA channel on port {GrooveServer.Config.Porta7K} — " +
                          $"{biblioteca7K.Count} charts, recording {GrooveServer.Config.Jogo7K}");
    // A biblioteca de 7K nao e' a de 5K, por isso a conta dos courses escondidos tambem nao.
    courseTable.Verificar(semBiblioteca ? null : biblioteca7K, GrooveServer.Config.Canal7K);
}

await Task.WhenAll(new[] { server.RunAsync(cts.Token) }
    .Concat(servidor7K is null ? Array.Empty<Task>() : new[] { servidor7K.RunAsync(cts.Token) }));








































