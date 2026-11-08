# Referência — OmniSync

Formatos de arquivo, locais e parâmetros do **OmniSync** (versão 2.0.2). Para o uso passo a passo, veja o [Manual do Usuário](MANUAL_DO_USUARIO.md); para a arquitetura, a [Documentação Técnica](DOCUMENTACAO_TECNICA.md).

---

## Índice
1. [Pasta de dados](#1-pasta-de-dados)
2. [`settings.json`](#2-settingsjson)
3. [Histórico de execuções (`history/<id>.json`)](#3-histórico-de-execuções-historyidjson)
4. [Log diário](#4-log-diário)
5. [Relatório CSV](#5-relatório-csv)
6. [Pastas no destino](#6-pastas-no-destino)
7. [Linha de comando](#7-linha-de-comando)
8. [Agendador do sistema](#8-agendador-do-sistema)
9. [Cofre de senhas](#9-cofre-de-senhas)

---

## 1. Pasta de dados

| Sistema | Pasta |
| --- | --- |
| Windows | `%LOCALAPPDATA%\OmniSync` |
| Linux | `~/.local/share/OmniSync` |

| Arquivo ou pasta | Conteúdo |
| --- | --- |
| `settings.json` | Tarefas, tarefa ativa, favoritos SFTP e chaves de servidor confirmadas (seção 2). Gravação atômica. |
| `history/<id>.json` | Últimas 30 execuções de cada tarefa (seção 3). |
| `logs/omnisync-AAAAMMDD.log` | Log completo do dia (seção 4). |
| `locks/<id>.lock` | Trava enquanto a tarefa sincroniza (interface ou agendamento). Aberto com exclusividade e apagado ao fechar; o sistema libera a trava mesmo se o processo morrer. |
| `snooze/<id>.txt` | Adiamento pendente do aviso agendado: 1ª linha, nome do disparo único no agendador; 2ª linha, horário previsto em hora local (`AAAA-MM-DDTHH:MM:SS`). |
| `crash.log` | Falhas inesperadas (exceção completa). |
| `selftest.log` | Resultado do autoteste (`--self-test`): `SELF_TEST_OK` ou `SELF_TEST_FAIL`. |

`<id>` é o identificador da tarefa (`Id` no `settings.json`, formato GUID).

**Pasta de instalação** (cópia fixa do executável, usada pelo agendamento): `%LOCALAPPDATA%\Programs\OmniSync` no Windows, `~/.local/share/Programs/OmniSync` no Linux.

---

## 2. `settings.json`

JSON em UTF-8, indentado; enums gravados como texto. Feche o OmniSync antes de editar à mão: a interface regrava o arquivo inteiro ao fechar.

### Raiz

| Campo | Tipo | Descrição |
| --- | --- | --- |
| `Profiles` | lista de tarefas | Tarefas salvas (abaixo). |
| `ActiveProfileId` | GUID ou `null` | Tarefa selecionada ao abrir. |
| `InstallOfferDismissed` | booleano | `true` depois de **Não perguntar de novo** na oferta de instalação. |
| `Favorites` | lista de favoritos | Servidores SFTP salvos (abaixo). |
| `KnownHostKeys` | objeto | Chaves de servidor confirmadas: `"servidor:porta"` → `"SHA256:…"`. Apagar uma entrada equivale a **Esquecer chave do servidor**. |
| `SourcePath`, `DestinationPath`, `UseSourceFolderNameInDestination`, `EnableDeepHashCheck`, `TolerateDstShift`, `ExcludePatterns` | — | Formato da versão 1.1 (uma tarefa só). Lidos uma vez e convertidos em tarefa; não são mais gravados. |

### Tarefa (`Profiles[]`)

| Campo | Tipo | Descrição |
| --- | --- | --- |
| `Id` | GUID | Identificador fixo; o agendamento usa este valor, por isso renomear a tarefa não quebra o agendamento. |
| `Name` | texto | Nome exibido. |
| `SourcePath` | texto | Origem: pasta local, UNC (`\\servidor\pasta`) ou `sftp://usuario@servidor[:porta]/caminho`. Aceita `%VARIAVEL%`, `~` e `$HOME`, expandidos ao usar. |
| `DestinationPath` | texto | Destino como escolhido, **sem** a subpasta da origem. |
| `UseSourceFolderNameInDestination` | booleano | Acrescenta o nome da pasta de origem ao destino (destino efetivo). |
| `EnableDeepHashCheck` | booleano | Compara por SHA-256 os arquivos de mesmo tamanho na análise. |
| `TolerateDstShift` | booleano | Considera iguais datas que diferem exatamente 1 h. |
| `ExcludePatterns` | texto | Padrões separados por `;` (`*` e `?`), aplicados a nomes de arquivos e pastas. |
| `Schedule` | objeto ou `null` | Agendamento (abaixo). |
| `LastRunUtc` | data UTC ou `null` | Última sincronização. |
| `LastOutcome` | `None`, `Success`, `Failures`, `Cancelled` | Resultado da última sincronização (cor do ponto na barra lateral). |
| `LastFailedCount` | inteiro | Falhas da última sincronização. |

### Agendamento (`Profiles[].Schedule`)

| Campo | Valores | Descrição |
| --- | --- | --- |
| `Enabled` | booleano | Ligado. |
| `Mode` | `Sync`, `AnalyzeAndNotify` | Sincronizar ou só analisar e avisar. |
| `Frequency` | `Daily`, `Weekly`, `AtLogon` | Todo dia, dias da semana ou 5 min após entrar no sistema. |
| `Hour`, `Minute` | 0–23, 0–59 | Horário (diário e semanal). |
| `Days` | lista de `Sunday` … `Saturday` | Dias, no modo semanal. |
| `ConflictPolicy` | `Keep`, `ReplaceWhenSourceNewer` | Conflitos sem decisão: manter destino, ou substituir só quando a origem é mais nova. |
| `RegisteredExecutable` | caminho | Executável registrado no agendador; serve para detectar agendamento quebrado. |

### Favorito (`Favorites[]`)

| Campo | Tipo | Descrição |
| --- | --- | --- |
| `Id` | GUID | Identificador. |
| `Name` | texto | Nome exibido no menu **Remoto…**. |
| `Address` | texto | Endereço `sftp://usuario@servidor[:porta]/caminho`. |
| `KeyFile` | caminho ou `null` | Arquivo de chave privada; sem ele, entra por senha. |
| `VerifyByReading` | booleano | Confere cada cópia relendo o arquivo pela rede em vez do SHA-256 no servidor. |

A senha **nunca** fica neste arquivo (seção 9).

---

## 3. Histórico de execuções (`history/<id>.json`)

Lista JSON, mais recente primeiro, com no máximo 30 registros por tarefa. Grava execuções manuais (interface) e agendadas.

| Campo | Descrição |
| --- | --- |
| `StartedUtc` | Início (UTC). |
| `Duration` | Duração (`hh:mm:ss.fffffff`). |
| `Scheduled` | `true` se veio do agendador. |
| `AnalyzeOnly` | `true` se só analisou. |
| `Outcome` | `ok`, `falhas`, `abortado`, `interrompido`, `ocupado` ou `erro`. |
| `PendingChanges` | Alterações encontradas pela análise (novos + conflitos + órfãos). |
| `Copied`, `Replaced`, `Trashed`, `Failed` | Copiados, substituídos, movidos para `lixo/`, falhas. |
| `VerifiedBySize` | Cópias conferidas só pelo tamanho (servidor sem SHA-256). |
| `Summary` | Resumo em uma linha. |

---

## 4. Log diário

- Arquivo `logs/omnisync-AAAAMMDD.log`, um por dia, UTF-8 **sem BOM**. No PowerShell 5.1, leia com `Get-Content -Encoding UTF8`, senão os acentos saem trocados.
- Cada linha: `AAAA-MM-DD HH:MM:SS.mmm <mensagem>`, hora local.
- Prefixos usados para filtrar:

| Prefixo | Significado |
| --- | --- |
| `[ERRO]` | Falha (arquivo, conexão, execução). Aparece no filtro **Avisos e erros**. |
| `[AVISO]` | Algo que exige atenção sem ser falha. Aparece no filtro **Avisos e erros**. |
| `[NOVO]`, `[SUBSTITUIR]`, `[CONFLITO]`, `[LIXO]` | Ação sobre um arquivo. |
| `[TEMP]` | Temporário de execução interrompida apagado. |
| `[RESTAURAÇÃO]` | Restauração de `lixo/`. |
| `[REMOTO]` | Conexão SFTP aberta pela interface. |
| `[CLI]` | Linha de comando: início, código de saída. |
| `[AGENDA]` | Agendamento e Adiar. |
| `[INSTALAÇÃO]` | Instalação na pasta fixa e atalhos. |

---

## 5. Relatório CSV

Gerado por **Exportar CSV** (plano ou resultado) ou por `--relatorio`. UTF-8 **com BOM**, separador `;`, abre direto no Excel. Valores com `;`, aspas ou quebra de linha vão entre aspas duplas.

| Coluna | Conteúdo |
| --- | --- |
| `caminho_relativo` | Caminho relativo à origem/destino efetivo. |
| `acao` | Ação em texto (como na lista). |
| `acao_codigo` | `novo`, `conflito`, `orfao`, `identico`. |
| `decisao` | Só conflitos: `Substituir`, `Manter destino`, `Perguntar`. |
| `decisao_codigo` | Só conflitos: `substituir`, `manter`, `perguntar`. |
| `status` | Situação do item (vazio no plano; resultado depois de sincronizar). |
| `erro` | `sim` ou `não`. |
| `tamanho_origem_bytes` | Tamanho na origem (vazio para órfãos). |
| `tamanho_destino_bytes_na_analise` | Tamanho no destino **na análise** (vazio para novos). |
| `modificacao_origem` | Data de modificação na origem, hora local, `AAAA-MM-DD HH:MM:SS`. |
| `modificacao_destino_na_analise` | Data no destino na análise, mesmo formato. |

---

## 6. Pastas no destino

| Item | Descrição |
| --- | --- |
| `<destino>/lixo/<caminho_relativo>` | Órfãos (existem só no destino). Nada é apagado. Em colisão, o nome ganha `_AAAAMMDD_HHMMSS` (e `_n`). |
| `<destino>/old/<caminho_relativo>` | Versões substituídas, com o nome original; data e hora entram no nome só em colisão. |
| `<arquivo>.omnisync-tmp` | Cópia em andamento. Renomeada ao final; se sobrar de uma execução interrompida, é apontada na análise e apagada na sincronização seguinte quando tiver mais de 1 h. |

Ignorados na análise, nos dois lados: `lixo/` e `old/` na raiz do destino (na raiz da origem, ignorados com aviso), `$RECYCLE.BIN`, `System Volume Information`, `#recycle`, `@eaDir`, `.Trash`, `.Trash-*`, `*.omnisync-tmp`, atalhos simbólicos e os padrões de exclusão da tarefa.

Regra de comparação: idênticos = mesmo tamanho e data com diferença de até 2 s (ou exatamente 1 h com **Tolerar 1 h**), ou mesmo SHA-256 com **Verificar conteúdo**.

---

## 7. Linha de comando

| Argumento | Sinônimo | Efeito |
| --- | --- | --- |
| `--listar` | `--list` | Lista as tarefas. |
| `--analisar "Tarefa"` | `--analyze` | Só analisa. |
| `--sincronizar "Tarefa"` | `--run` | Analisa e sincroniza. |
| `--conflitos manter` \| `origem-mais-nova` | — | Política para conflitos sem decisão (padrão `manter`). |
| `--relatorio arquivo.csv` | — | Grava o CSV (seção 5). |
| `--detalhado` | — | Todas as linhas do registro no console. |
| `--agendado` | — | Usado pelo agendador: histórico como agendado e aviso ao fim. |
| `--versao` | `--versão`, `--version` | Mostra `OmniSync X.Y.Z`. |
| `--ajuda` | `--help`, `-h`, `/?` | Ajuda. |
| `--self-test` | — | Autoteste da interface (desenvolvimento e CI). |

"Tarefa" aceita o nome (sem diferenciar maiúsculas nem acentos) ou o `Id`. Códigos de saída 0–8 e exemplos no [manual, seção 9](MANUAL_DO_USUARIO.md#9-linha-de-comando-sem-janela).

---

## 8. Agendador do sistema

| Sistema | Agendamento da tarefa | Adiar (disparo único) |
| --- | --- | --- |
| Windows | Agendador de Tarefas, pasta `\OmniSync`, item `<id>` | Item `\OmniSync\<id>-adiado-AAAAMMDD-HHMMSS` |
| Linux | `~/.config/systemd/user/omnisync-<id>.service` e `.timer` | Timer transitório criado por `systemd-run --user` |

Os dois chamam o executável da pasta de instalação com `--sincronizar <id> --agendado --conflitos manter|origem-mais-nova`, ou `--analisar <id> --agendado` no modo *Só analisar*.

---

## 9. Cofre de senhas

| Sistema | Onde fica | Nome da entrada |
| --- | --- | --- |
| Windows | Gerenciador de Credenciais › Credenciais do Windows (credencial genérica) | `OmniSync:sftp:usuario@servidor:porta` |
| Linux | Chaveiro do GNOME/KDE via `secret-tool` (libsecret) | Atributos `service=omnisync` e `account=OmniSync:sftp:usuario@servidor:porta` |

Sem cofre disponível (Linux sem D-Bus ou chaveiro), a senha vale só enquanto o OmniSync estiver aberto, e a execução agendada sai com o código 7.
