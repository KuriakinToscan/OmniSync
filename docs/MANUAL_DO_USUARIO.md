# Manual do Usuário — OmniSync

Manual do **OmniSync**, sincronizador visual de arquivos para **Windows e Linux** com proteção automática de arquivos órfãos (`lixo/`) e histórico de versões substituídas (`old/`).

---

## Índice
1. [Visão Geral da Interface](#1-visão-geral-da-interface)
2. [Passo a Passo de Uso](#2-passo-a-passo-de-uso)
3. [NAS, Rede e Servidores SFTP](#3-nas-rede-e-servidores-sftp)
4. [Entendendo as Categorias de Arquivos](#4-entendendo-as-categorias-de-arquivos)
5. [Resolvendo Conflitos de Arquivos](#5-resolvendo-conflitos-de-arquivos)
6. [Restaurando Arquivos das Pastas `lixo/` e `old/`](#6-restaurando-arquivos-das-pastas-lixo-e-old)
7. [Garantias de Segurança](#7-garantias-de-segurança)
8. [Perguntas Frequentes e Solução de Problemas](#8-perguntas-frequentes-e-solução-de-problemas)
9. [Linha de Comando (sem janela)](#9-linha-de-comando-sem-janela)
10. [Instalação e Agendamento](#10-instalação-e-agendamento)

---

## 1. Visão Geral da Interface

A janela tem um cabeçalho, uma barra lateral e uma área principal que muda conforme a etapa.

- **Cabeçalho**: versão do OmniSync (ex.: `v2.0.2`), nome da tarefa ativa (clique no nome ou no lápis para renomear), **Registro** (janela redimensionável com o log da sessão, filtro **Avisos e erros** e **Copiar**) e **Logs** (pasta com o histórico completo).
- **Barra lateral · Tarefas**: cada tarefa guarda origem, destino e opções. O ponto colorido indica o estado:
  - cinza: sem análise ou nunca sincronizado;
  - laranja: plano analisado aguardando sincronização;
  - verde-azulado: sincronizando;
  - verde: última sincronização sem falhas;
  - vermelho: última sincronização com falhas ou interrompida.

  O botão **+** (ou Ctrl+N) cria uma tarefa. Para excluir, use o **X** do cartão (aparece ao passar o mouse) ou o botão direito › **Excluir tarefa**; só a configuração é apagada, nenhum arquivo é tocado. Duplo clique ou botão direito › **Renomear** muda o nome.
- **Barra lateral · Pastas de segurança**: **Restaurar de lixo/**, **Abrir lixo/** e **Abrir old/** da tarefa ativa. Os atalhos não criam as pastas: se ainda não existirem, o aplicativo avisa.

A área principal tem quatro telas:

1. **Configuração**: origem, destino e opções. Aparece enquanto não há análise.
   - **Criar subpasta com o nome da origem** *(marcado por padrão)*: com Origem `E:\Fotos` e Destino `G:\`, a sincronização ocorre em `G:\Fotos`, sem afetar outras pastas de `G:\`. O campo guarda só a pasta escolhida (`G:\`); a linha **Sincroniza em** abaixo dele mostra o destino efetivo.
   - **Verificar conteúdo (SHA-256)**: arquivos de mesmo tamanho são comparados pelo conteúdo, e não pela data. Mais lento, porque lê os arquivos inteiros dos dois lados; útil quando as datas não são confiáveis. *(desmarcado por padrão)*
   - Independentemente dessa opção, **toda cópia e substituição é conferida por SHA-256** antes de ocupar o lugar no destino: se a cópia não bater com a origem, o aplicativo tenta de novo e, se persistir, registra a falha sem tocar no arquivo do destino.
   - **Tolerar 1 h (horário de verão)**: considera iguais arquivos cuja data difere exatamente 1 hora. Útil com pendrives e HDs em FAT/exFAT, que gravam hora local.
   - **Ignorar**: padrões separados por ponto e vírgula, aplicados ao nome de arquivos e pastas nos dois lados (`*` e `?` como curingas). Padrão: `Thumbs.db; desktop.ini; .DS_Store; ~$*`.
2. **Plano analisado**:
   - no topo, origem → destino, as opções ativas e os botões **Alterar** (volta à configuração) e **Reanalisar** (F5);
   - o **resumo** com o total de alterações, uma barra proporcional (azul = novos, laranja = conflitos, violeta = `lixo/`) e os filtros por categoria. Clicar de novo no filtro ativo volta a mostrar todas as alterações. O link **avisos da análise** aparece quando algo exige atenção (pastas ilegíveis, nomes reservados, arquivos que diferem só por maiúsculas);
   - **Pastas com alterações**: a árvore mostra quantas alterações (e conflitos, em laranja) há em cada pasta. Selecionar uma pasta filtra a lista;
   - a **lista de arquivos**, com busca por caminho. Para conflitos, a coluna Ação mostra a decisão atual;
   - o **painel de detalhes** do arquivo selecionado: tamanho e data nos dois lados (o mais recente fica destacado), o que vai acontecer e, para conflitos, a decisão;
   - a **barra inferior** resume o plano (copiar, substituir, manter, sem decisão, mover para `lixo/`) e traz **Exportar CSV** e **Revisar e sincronizar** (Ctrl+Enter).
3. **Execução**: percentual (fica em no máximo 99% enquanto houver arquivo por terminar; durante um conflito aparece a faixa **Aguardando sua decisão**), **Volume** (soma dos bytes já gravados e total a gravar) e **Arquivos** processados, velocidade, tempo restante, andamento por fase (cópias, com o volume total a copiar; conflitos; `lixo/`), arquivo atual e o registro da atividade, com filtro **Avisos e erros**. **Cancelar** (Esc) interrompe sem deixar arquivo parcial.
4. **Resultado**: balanço (copiados, substituídos, mantidos, movidos para `lixo/`, falhas), a lista do que não foi feito com o motivo, **Tentar de novo** e atalhos para `lixo/`, `old/`, relatório CSV e **Analisar novamente** (F5). **Ver arquivos** volta à lista com o estado de cada item.

Em janelas com menos de 1.240 px de largura, a árvore de pastas é ocultada para dar espaço à lista e ao painel de detalhes.

---

## 2. Passo a Passo de Uso

### Etapa 1: escolher ou criar a tarefa
1. Selecione uma tarefa na barra lateral, ou crie uma com **+**.
2. Em **Origem**, clique em **Escolher pasta…** (ou cole o caminho). A tarefa recebe o nome da pasta de origem; você pode renomeá-la no cabeçalho. **Abrir no Explorer** (ou no gerenciador de arquivos) mostra a pasta.
3. Faça o mesmo em **Destino**. Com **Criar subpasta com o nome da origem** marcada, o nome da pasta de origem é acrescentado ao destino (ex.: `G:\MinhaPasta`). Se origem e destino forem a mesma pasta, ou uma estiver dentro da outra, um aviso vermelho aparece na hora e **Analisar** fica bloqueado.

### Etapa 2: analisar (Dry-Run)
1. Clique em **Analisar** (ou F5). Nenhum arquivo é alterado.
2. Leia os avisos da análise, se houver, e confira o resumo.
3. Use a árvore, os filtros e a busca para revisar o plano. Para conflitos, decida no painel de detalhes ou pelo menu **Decidir em lote**.

**Alterar** volta à configuração sem perder o plano: enquanto nada mudar, **Voltar ao plano** retoma o plano como estava, com as decisões. Se você mudar uma pasta ou uma opção, o plano é descartado e uma nova análise é exigida.

### Etapa 3: sincronizar
1. Clique em **Revisar e sincronizar** (ou Ctrl+Enter).
2. Revise a janela **Revise antes de sincronizar**: quantos arquivos serão copiados, substituídos, mantidos e movidos para `lixo/`, e quantos conflitos ainda serão perguntados.
3. Se a operação for mover uma parte grande do destino para `lixo/` (20% ou mais dos arquivos, com mínimo de 10, ou 1.000 arquivos ou mais), a janela exibe um alerta vermelho e só libera o botão depois que você marcar a confirmação. Isso costuma indicar destino escolhido errado.
4. Acompanhe a tela de execução. Ao final, a tela de resultado mostra o balanço. Itens com falha podem ser reprocessados em **Tentar de novo**. Na mesma tela, **Abrir origem** e **Abrir destino** mostram as pastas da tarefa.
5. **Exportar relatório CSV** gera o relatório da análise e do resultado (UTF-8, separado por ponto e vírgula, abre direto no Excel). O cabeçalho usa nomes ASCII (`caminho_relativo`, `acao`, `acao_codigo`, `decisao`, `decisao_codigo`…); as colunas `*_na_analise` mostram o destino como estava antes da sincronização. Os códigos são em português, sem acento: `acao_codigo` = `novo`, `conflito`, `orfao`, `identico`; `decisao_codigo` = `substituir`, `manter`, `perguntar`. Na barra de busca, o **X** (ou Esc) limpa o texto.

---

## 3. NAS, Rede e Servidores SFTP

- **Windows**: use uma unidade mapeada (ex.: `Z:\Backups`) ou digite o caminho UNC:
  ```text
  \\192.168.1.100\compartilhamento\meus_arquivos
  \\meu-nas\arquivos
  ```
  Ao usar **Procurar...** em "Rede", entre no servidor e escolha uma **pasta compartilhada específica**, não o ícone raiz "Rede".
- **Linux**: use o ponto de montagem do compartilhamento CIFS/SMB ou NFS (ex.: `/mnt/nas/backup` ou `/run/user/1000/gvfs/smb-share:...`).

### Servidor SFTP (NAS, servidor Linux, OpenSSH do Windows)

A origem e o destino aceitam um endereço SFTP:

```text
sftp://usuario@servidor/pasta
sftp://usuario@servidor:2222/volume1/backup
```

- **VPN:** se o nome do servidor não for encontrado pela VPN (comum em redes corporativas), use o endereço IP no lugar do nome, por exemplo `sftp://usuario@10.0.0.5/share/pasta`.
- A porta padrão é 22. O caminho é o caminho completo da pasta no servidor (no OpenSSH do Windows, algo como `/C:/Users/usuario/Backup`).
- **Remoto…**, ao lado de **Escolher pasta…**, lista os **favoritos** (servidores salvos com nome) e abre o cadastro. No cadastro:
  - informe servidor, porta, usuário, pasta e a forma de entrar (senha ou arquivo de chave, como `id_ed25519`);
  - use **Testar conexão**: ela conecta, lista a pasta e grava, renomeia e apaga um arquivo de teste. O resultado também diz como as cópias serão conferidas (veja **Conferência** abaixo).
- **Senha:** fica no cofre do sistema, nunca no `settings.json`. No Windows é o Gerenciador de Credenciais; no Linux, o chaveiro do GNOME/KDE. Sem cofre, a senha vale só enquanto o OmniSync estiver aberto.
- **Primeira conexão:** o OmniSync mostra a impressão digital da chave do servidor (`SHA256:…`) e pede confirmação. Se a chave mudar depois, a conexão é recusada; isso protege contra um servidor falso. Se o servidor foi reinstalado, use **Esquecer chave do servidor** no favorito.
- **Garantias:** são as mesmas do disco local. Órfãos vão para `lixo/` e versões substituídas para `old/`, por renomeação no servidor. Cada arquivo é gravado como temporário e renomeado.
- **Conferência:** cada cópia é conferida antes de ocupar o lugar no destino.
  - Normalmente, o próprio servidor calcula o SHA-256 do arquivo gravado (`sha256sum` no Linux e na maioria dos NAS; `Get-FileHash` no OpenSSH do Windows), e o OmniSync compara com o da origem. O arquivo não volta pela rede.
  - Servidor que só aceita SFTP, sem comandos (comum com pastas isoladas por `chroot`), fica com a conferência pelo **tamanho**. O registro e o histórico dizem quantas cópias foram conferidas assim.
  - No favorito, **Conferir cada cópia relendo o arquivo pela rede** troca para releitura com SHA-256, ao custo de dobrar o tráfego.
- **Queda do servidor:** uma queda curta reconecta sozinha e repete o arquivo. Se o servidor não voltar, a sincronização para: o arquivo em andamento falha, os demais ficam como **não executados** e o resultado aparece como interrompido. O que já foi copiado fica no destino, e a próxima execução continua daí.
- **Desempenho:** o envio não espera a confirmação de cada pedaço, então arquivos grandes chegam perto da velocidade do link mesmo numa VPN com latência alta. Cada arquivo ainda custa um número fixo de idas e voltas ao servidor (criar, gravar, conferir, data, renomear); para diluir esse custo, o OmniSync copia até 4 arquivos novos ao mesmo tempo no destino SFTP. Mesmo assim, pastas com dezenas de milhares de arquivos pequenos ficam bem mais lentas que no disco local ou na rede do escritório (estimativa).
- **Agendamento:** a execução agendada usa a senha do cofre e a chave já confirmada, sem perguntar nada. Se faltar uma delas, ela para com o código 7 e avisa. Abra o OmniSync e analise a tarefa uma vez para resolver.

---

## 4. Entendendo as Categorias de Arquivos

| Categoria | Quando ocorre | O que o OmniSync faz |
| :--- | :--- | :--- |
| **Idêntico** | Mesmo tamanho e data (± 2 s) nos dois lados, ou mesmo SHA-256. | **Nada.** |
| **Novo (copiar)** | Existe só na Origem. | **Copia** para o Destino preservando subpastas e data de modificação. |
| **Órfão (p/ lixo/)** | Existe só no Destino. | **Move** para `<Destino>/lixo/<caminho_relativo>`. Nunca apaga. |
| **Conflito** | Mesmo caminho nos dois lados, com tamanho, data ou conteúdo diferentes. A tabela indica qual lado é mais recente. | **Substitui** (versão atual vai para `<Destino>/old/`) ou **ignora**, conforme sua decisão. |

---

## 5. Resolvendo Conflitos de Arquivos

### Antes de sincronizar (recomendado)
- **Painel de detalhes**: selecione o arquivo na lista e escolha **Substituir** (a versão atual do destino vai para `old/`), **Manter destino** ou **Perguntar na hora**. O lado mais recente fica destacado.
- **Menu Decidir em lote**:
  - **Mais recente vence**: substitui quando a origem é mais nova; mantém o destino quando ele é mais novo ou quando as datas coincidem.
  - **Substituir todos**: versões atuais do destino vão para `old/`.
  - **Manter o destino em todos**.

Conflitos sem decisão aparecem em laranja na lista e são contados na barra inferior.

**Decisões guardadas:** se o plano for descartado (nova análise, troca de tarefa), as decisões tomadas voltam na análise seguinte da mesma origem e destino para os arquivos que não mudaram de tamanho nem de data nos dois lados. Arquivo que mudou volta como **Perguntar na hora**.

### Durante a sincronização
Conflitos ainda em **Perguntar na hora** abrem uma janela comparativa com tamanho e data dos dois lados e um selo **mais recente**. Se o destino for mais novo, um alerta avisa que substituir trocaria uma versão mais nova por uma mais antiga. A opção **Aplicar aos demais conflitos** reaproveita a escolha.

---

## 6. Restaurando Arquivos das Pastas `lixo/` e `old/`

- **Restaurar de lixo/** (barra lateral): abre a lista dos arquivos em `<Destino>/lixo/` da tarefa ativa, com seleção individual. O resultado aparece em uma janela de aviso. Os marcados voltam ao local original.
  - Se o local original já estiver ocupado, o arquivo **permanece em `lixo/`** e é contado como "mantido". Nada é sobrescrito nem excluído.
  - Arquivos que receberam sufixo de data em `lixo/` (ex.: `foto_20261028_114500.jpg`) voltam com o nome original quando ele está livre.
  - Só pastas que ficaram vazias são removidas de `lixo/`.
  - Depois da restauração, faça uma nova análise.
- **Abrir lixo/** e **Abrir old/**: abrem as pastas no gerenciador de arquivos. Com destino SFTP, que o gerenciador de arquivos não abre, mostram o endereço da pasta no servidor, dizem se ela existe e oferecem **Copiar endereço** para colar num programa de SFTP (ex.: WinSCP ou FileZilla). O mesmo vale para **Abrir no Explorer**, **Abrir origem** e **Abrir destino**. Em `old/`, a versão substituída mantém o nome; data e hora só entram no nome quando já existe um arquivo igual em `old/` (ex.: `relatorio_20261028_114500.xlsx`).
- **Pasta de rede que não responde**: os botões que abrem pastas esperam no máximo 5 s. Se a unidade de rede (ex.: `Z:` de um NAS desligado) não responder, o aplicativo avisa e continua respondendo, em vez de travar até o Windows desistir (cerca de 1 min). O próprio Windows (Explorer, PowerShell) pode continuar lento enquanto a unidade estiver caída; reconecte-a ou remova-a com `net use Z: /delete`.

---

## 7. Garantias de Segurança

- **Nenhuma cópia parcial no destino**: cada arquivo é gravado primeiro como `nome.omnisync-tmp` e só então renomeado. Cancelamento, queda de rede ou falha removem o temporário. Se o programa fechar ou a conexão cair antes disso, o temporário que sobrar é apontado na análise seguinte e apagado na sincronização, desde que tenha mais de 1 hora (um temporário recente pode ser a cópia em andamento de outra tarefa com o mesmo destino).
- **Substituição segura**: a nova versão é copiada antes de a atual ir para `old/`. Se a etapa final falhar, a versão anterior é devolvida ao lugar.
- **Nunca sobrescreve**: se um arquivo surgir no destino depois da análise, o item falha com aviso em vez de sobrescrever.
- **Origem e destino aninhados são bloqueados**: destino dentro da origem (ou o contrário) é recusado na análise.
- **Pastas ilegíveis não geram órfãos**: se uma pasta da origem não puder ser lida, os arquivos correspondentes no destino não são movidos para `lixo/`.
- **Nomes reservados**: pastas chamadas `lixo` ou `old` na **raiz da origem** são ignoradas com aviso (colidiriam com as pastas de segurança do destino). Renomeie-as para sincronizá-las. Em subpastas, esses nomes funcionam normalmente.
- **Maiúsculas/minúsculas no Linux**: arquivos que diferem só na caixa (`Foto.JPG` e `foto.jpg`) são tratados como distintos, com aviso, pois colidiriam em destinos NTFS/exFAT/SMB.

---

## 8. Perguntas Frequentes e Solução de Problemas

**1. As pastas `lixo/` e `old/` entram em loop nas próximas sincronizações?**
Não. O motor ignora `<Destino>/lixo` e `<Destino>/old`, além de pastas de sistema (`$RECYCLE.BIN`, `System Volume Information`, `#recycle` e `@eaDir` de NAS, `.Trash` e `.Trash-*` do Linux).

**2. O que acontece se a rede com o NAS oscilar durante a cópia?**
Falhas transitórias de E/S são repetidas até 3 vezes, com espera progressiva. Erros definitivos (permissão negada, caminho inexistente) não são repetidos e aparecem na tela de resultado e no filtro **erros** do plano.

**3. Onde ficam os registros?**
- Log completo de cada dia: `omnisync-AAAAMMDD.log` na pasta de logs (botão **Logs**).
- Falhas inesperadas: `crash.log`.
- Pasta de dados: `%LOCALAPPDATA%\OmniSync` no Windows; `~/.local/share/OmniSync` no Linux. As tarefas salvas ficam em `settings.json` na mesma pasta. A lista completa de arquivos está na [Referência](REFERENCIA.md#1-pasta-de-dados).

**4. Todos os arquivos de um pendrive aparecem como conflito depois da mudança de horário.**
Ative **Tolerar 1 h (horário de verão)**. Mídias FAT/exFAT gravam hora local e mudam 1 hora quando o fuso muda.

**5. Como sei qual versão estou usando?**
A versão aparece no cabeçalho, ao lado do nome OmniSync (ex.: `v2.0.2`). Pelo terminal: `OmniSync.exe --versao | Out-Host`.

**6. O Windows mostra "O Windows protegeu o computador" ao abrir o executável.**
É o SmartScreen: o executável ainda não é assinado digitalmente, e o aviso aparece em arquivos baixados sem assinatura. Se o arquivo veio do repositório oficial ou de quem mantém o OmniSync, clique em **Mais informações** › **Executar assim mesmo**. A assinatura do executável está em estudo.

---

## 9. Linha de Comando (sem janela)

As tarefas salvas na interface também podem ser analisadas e sincronizadas pelo terminal, sem abrir a janela e sem perguntas. É a base do agendamento.

| Comando | O que faz |
| --- | --- |
| `OmniSync.exe --listar` | Lista as tarefas salvas, com origem, destino e última execução. |
| `OmniSync.exe --analisar "Volume"` | Só analisa e mostra o plano (novos, conflitos, `lixo/`, idênticos). Não altera nada. |
| `OmniSync.exe --sincronizar "Volume"` | Analisa e sincroniza. |
| `OmniSync.exe --ajuda` | Mostra a ajuda. |
| `OmniSync.exe --versao` | Mostra a versão (ex.: `OmniSync 2.0.2`). |

O nome da tarefa não diferencia maiúsculas nem acentos (`"volume"` encontra `Volume`). Se houver duas tarefas com o mesmo nome, o comando é recusado e mostra os identificadores; use o identificador no lugar do nome.

**Opções:**
- `--conflitos manter` (padrão): mantém o arquivo do destino em todo conflito ainda sem decisão.
- `--conflitos origem-mais-nova`: substitui só quando a origem é mais nova; a versão anterior vai para `old/`. Destino mais novo ou de mesmo horário é **sempre mantido** pela linha de comando.
- `--relatorio C:\Temp\volume.csv`: grava o CSV do plano (ao analisar) ou do resultado (ao sincronizar).
- `--detalhado`: mostra no console todas as linhas do registro, arquivo por arquivo. Sem ela aparecem o plano, o progresso a cada 5 s, avisos, erros e o resultado.

**Segurança:** se a análise indicar remoção em massa (pelo menos 1.000 órfãos, ou 10 ou mais representando 20% do destino), a sincronização é **abortada sem alterar nada**. Confira o destino da tarefa e, se estiver certo, sincronize pela interface. Ctrl+C interrompe com segurança: nenhum arquivo parcial fica no destino.

**Códigos de saída** (para scripts e agendador):

| Código | Significado |
| --- | --- |
| 0 | Concluído sem falhas |
| 1 | Concluído com falhas (ou relatório não gravado) |
| 2 | Abortado por remoção em massa; nada foi alterado |
| 3 | Argumento, tarefa ou caminho inválido; nada foi alterado |
| 4 | Interrompido (Ctrl+C) |
| 5 | Erro inesperado (detalhes no log) |
| 6 | A tarefa já está sendo sincronizada (pela interface ou por outro agendamento); nada foi feito |
| 7 | Conexão remota precisa de você: falta a senha no cofre, a senha guardada foi recusada, ou a chave do servidor é nova ou mudou. Abra o OmniSync e analise a tarefa uma vez. Antes da cópia, nada foi feito; se a recusa veio ao reconectar no meio, o que já foi copiado fica |
| 8 | Servidor remoto inacessível (endereço, porta, rede) ou arquivo de chave ilegível. Se ele cair no meio da sincronização, o que já foi copiado fica e o resto espera a próxima execução |

**No PowerShell (Windows)**, acrescente `| Out-Host` para o terminal esperar o fim e mostrar a saída; o código fica em `$LASTEXITCODE`:

```powershell
.\OmniSync.exe --sincronizar "Volume" | Out-Host ; $LASTEXITCODE
```

Sem `| Out-Host` o comando funciona, mas o PowerShell volta ao prompt antes de o OmniSync terminar (o executável é um aplicativo de janela). Todas as linhas vão também para o log diário (`%LOCALAPPDATA%\OmniSync\logs`), e a execução aparece na barra lateral da interface como "Sincronizado hoje, HH:MM" (com a interface aberta, ao voltar para a janela).

A opção `--agendado` é usada pelo agendamento (seção 10): marca a execução como agendada no histórico e, ao fim, mostra uma janela de aviso quando há algo a avisar.

---

## 10. Instalação e Agendamento

### Instalação na pasta fixa
O OmniSync é um executável único, sem instalador. Quando é aberto fora da pasta fixa do usuário (`%LOCALAPPDATA%\Programs\OmniSync` no Windows, `~/.local/share/Programs/OmniSync` no Linux), ele oferece copiar-se para lá e criar os atalhos na **área de trabalho** e no **menu Iniciar** (no Linux, no menu de aplicativos). Não precisa de administrador.

- **Instalar**: copia o executável, cria os atalhos marcados e oferece abrir a versão instalada (a janela atual fecha). Depois disso, o arquivo baixado pode ser apagado.
- **Agora não**: pergunta de novo na próxima abertura. Com **Não perguntar de novo**, a oferta deixa de aparecer.
- **Nova versão**: ao abrir um executável diferente do instalado (por exemplo, uma versão baixada depois), o OmniSync oferece **Atualizar** a cópia da pasta fixa. Feche a versão instalada antes, se estiver aberta.

O agendamento sempre chama o executável da pasta fixa, que não muda quando você baixa uma nova versão. Por isso, ligar um agendamento exige a instalação.

### Agendar uma tarefa
No cartão **Agendamento**, na tela de configuração da tarefa:

1. Ligue a chave **Ligado**.
2. **O que fazer**: *Sincronizar* (sem perguntas) ou *Só analisar e avisar* (não altera nada; avisa quando há alterações pendentes).
3. **Quando**: *Todo dia*, *Dias da semana* (marque os dias) ou *Ao entrar no sistema* (5 minutos após o logon).
4. **Horário**, para as opções diária e semanal: digite `18:30`, `1830`, `7h05` ou `7` (Enter confirma e salva), ou clique no botão do relógio ao lado do campo. No relógio, clique na hora (anel externo 00–11, interno 12–23) e depois nos minutos; um clique arredonda para 5 em 5 e arrastando dá para escolher minuto a minuto. As setas do teclado também mudam o valor. **OK** preenche o campo e salva.
5. **Em conflito, substituir quando a origem for mais nova**: desmarcado, o destino é sempre mantido. Marcado, a versão anterior vai para `old/`. Destino mais novo nunca é substituído.
6. Clique em **Salvar agendamento**. Para desligar, desligue a chave e salve.

O agendamento é registrado no **Agendador de Tarefas do Windows** (pasta `OmniSync`) ou, no Linux, como timer do systemd do usuário. Ele roda com o OmniSync fechado, enquanto você estiver com a sessão iniciada, inclusive na bateria. Se o computador estiver desligado no horário, a execução acontece assim que possível. A tarefa agendada ganha um ícone de relógio na barra lateral.

As regras sem perguntas são as da linha de comando: conflitos seguem a opção do cartão, e remoção em massa **aborta sem alterar nada**. Se a interface estiver sincronizando a mesma tarefa, a execução agendada não faz nada (código 6), e vice-versa.

### Aviso ao fim
Ao terminar, uma janela pequena aparece no canto inferior direito quando há algo a dizer: alterações sincronizadas, alterações pendentes (modo *Só analisar*), falhas, sincronização abortada ou erro. Execuções sem nada a fazer não mostram aviso. **Abrir OmniSync** abre a interface.

**Adiar:** quando repetir mais tarde pode resolver (alterações pendentes no modo *Só analisar*, sincronização com falhas, servidor inacessível), o aviso traz o botão **Adiar ▾**, com 15 min, 1 h, 4 h ou **Escolher horário…** (relógio; se o horário já passou hoje, vale para amanhã). O OmniSync registra uma execução única no agendador do sistema, que repete a mesma tarefa no horário escolhido e só avisa de novo se ainda houver o que avisar. Cada tarefa tem no máximo um adiamento pendente: adiar de novo substitui o anterior, e a execução normal do agendamento cancela o pendente. Avisos em que repetir não mudaria nada (sincronizada sem falhas, remoção em massa abortada, senha ou chave do servidor pendente) não têm Adiar.

Avisos de problema e avisos com **Adiar** ficam na tela até você escolher ou fechar; os demais fecham sozinhos após 30 s (o tempo para enquanto o mouse estiver sobre a janela).

### Histórico e verificação
O cartão mostra a próxima execução, a última execução agendada e as 5 execuções mais recentes (manuais e agendadas). O histórico guarda as 30 últimas por tarefa.

Ao abrir, o OmniSync confere cada agendamento ligado. Se o executável registrado foi apagado ou a entrada foi removida do Agendador de Tarefas, ele avisa e oferece **Corrigir** (registrar de novo apontando para a versão instalada). Excluir uma tarefa remove também o seu agendamento e o histórico.
