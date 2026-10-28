# 📖 Manual do Usuário — OmniSync

Bem-vindo ao manual oficial do **OmniSync**, o sincronizador visual de arquivos para **Windows e Linux** com proteção automática de arquivos órfãos (`lixo/`) e histórico de versões substituídas (`old/`).

---

## Índice
1. [Visão Geral da Interface](#1-visão-geral-da-interface)
2. [Passo a Passo de Uso](#2-passo-a-passo-de-uso)
3. [Sincronizando com NAS / Rede (SMB / NFS)](#3-sincronizando-com-nas--rede-smb--nfs)
4. [Entendendo as Categorias de Arquivos](#4-entendendo-as-categorias-de-arquivos)
5. [Resolvendo Conflitos de Arquivos](#5-resolvendo-conflitos-de-arquivos)
6. [Restaurando Arquivos das Pastas `lixo/` e `old/`](#6-restaurando-arquivos-das-pastas-lixo-e-old)
7. [Perguntas Frequentes e Solução de Problemas](#7-perguntas-frequentes-e-solução-de-problemas)

---

## 1. Visão Geral da Interface

A janela principal do OmniSync é dividida em 5 áreas principais:

1. **Painel de Diretórios e Ações (Topo)**:
   - **📁 Diretório de Origem**: Pasta que contém os arquivos originais que você deseja espelhar.
   - **🖧 Diretório de Destino (Local ou NAS)**: Unidade ou pasta onde a cópia sincronizada será mantida.
   - **Criar/usar subpasta com nome da origem** *(Marcado por padrão)*: Garante que, ao escolher uma pasta como `E:\Fotos` na Origem e selecionar um disco como `G:\` no Destino, a sincronização ocorra dentro de `G:\Fotos`, sem afetar nenhuma outra pasta existente em `G:\`.
   - **Verificação SHA-256**: Quando ativada, calcula a assinatura criptográfica de arquivos que possuem o mesmo tamanho para garantir que cada byte do conteúdo seja idêntico.
2. **Cartões de Resumo (Indicadores)**:
   - Exibe instantaneamente a contagem de arquivos **Novos a Copiar** (e o volume total em MB/GB), **Idênticos (Ignorados)**, **Conflitos** e **Órfãos (Para o Lixo)**.
3. **Abas de Inspeção**:
   - **📋 Prévia de Arquivos**: Tabela interativa com filtros rápidos (`Todos`, `Novos`, `Conflitos`, `Órfãos (Lixo)`, `Idênticos`).
   - **📜 Registro de Operações (Log)**: Histórico cronológico detalhado de todas as ações executadas.
4. **Painel de Progresso em Tempo Real**:
   - Mostra o arquivo em processamento, quantidade de arquivos concluídos, velocidade atual de cópia (`MB/s`) e tempo restante estimado (`ETA`).
5. **Barra Inferior de Utilitários**:
   - **↩ Restaurar de lixo/**: Devolve automaticamente arquivos movidos para a pasta `lixo/` aos seus locais originais.
   - **Abrir destino/lixo/ ↗** e **Abrir destino/old/ ↗**: Abre diretamente as pastas de segurança no Windows Explorer ou gerenciador de arquivos do Linux.

---

## 2. Passo a Passo de Uso

### Etapa 1: Selecionar Origem e Destino
1. Clique em **Procurar...** no campo **Diretório de Origem** e escolha a pasta desejada (ou cole o caminho diretamente na caixa de texto).
2. Clique em **Procurar...** no campo **Diretório de Destino** e selecione a unidade ou pasta de destino.
   - *Observação*: Com a opção **"Criar/usar subpasta com nome da origem"** marcada, o OmniSync preencherá automaticamente o nome da pasta de origem ao final do caminho de destino (ex.: `G:\MinhaPasta`).

### Etapa 2: Analisar Diferenças (Dry-Run)
1. Clique no botão **🔍 1. Analisar Diferenças (Dry-Run)**.
2. O aplicativo fará uma leitura rápida das duas pastas **sem alterar, mover ou apagar nenhum arquivo**.
3. Confira os números nos cartões de resumo e use os botões de filtro (`Novos`, `Conflitos`, `Órfãos (Lixo)`) para revisar o que será feito.

### Etapa 3: Iniciar Sincronização
1. Após revisar a prévia, clique em **▶ 2. Iniciar Sincronização**.
2. Acompanhe o progresso na barra inferior. Caso precise interromper a operação a qualquer momento, clique no botão **✕ Cancelar**.

---

## 3. Sincronizando com NAS / Rede (SMB / NFS)

O OmniSync suporta unidades mapeadas e caminhos de rede diretos:

- **No Windows**:
  - Você pode usar uma letra de unidade mapeada (ex.: `Z:\Backups`) ou digitar diretamente o caminho UNC do servidor e pasta compartilhada no campo de texto:
    ```text
    \\192.168.1.100\compartilhamento\meus_arquivos
    \\meu-nas\arquivos
    ```
  - **Importante**: Ao usar o botão *Procurar...* na seção "Rede" do Windows Explorer, entre dentro do servidor NAS e selecione uma **pasta compartilhada específica** (não selecione apenas o ícone raiz "Rede").
- **No Linux**:
  - Utilize o ponto de montagem local do compartilhamento CIFS/SMB ou NFS (ex.: `/mnt/nas/backup` ou `/run/user/1000/gvfs/smb-share:...`).

---

## 4. Entendendo as Categorias de Arquivos

| Categoria | Quando ocorre | O que o OmniSync faz |
| :--- | :--- | :--- |
| **Idêntico (Ignorar)** | O arquivo existe na Origem e no Destino com o mesmo tamanho e data (ou mesmo hash SHA-256). | **Nada.** Economiza tempo e evita desgaste de disco/rede. |
| **Novo (Copiar)** | O arquivo existe na Origem, mas ainda não existe no Destino. | **Copia** o arquivo para o Destino mantendo a mesma estrutura de subpastas e data de modificação. |
| **Órfão (Mover p/ Lixo)** | O arquivo existe no Destino, mas não existe mais na Origem. | **Move** o arquivo para `<Destino>/lixo/<caminho_relativo>` (nunca apaga permanentemente). |
| **Conflito** | O arquivo existe na Origem e no Destino com o mesmo nome, mas tamanho, data ou conteúdo diferentes. | **Pergunta a você** se deseja Ignorar ou Substituir (guardando a versão anterior em `<Destino>/old/`). |

---

## 5. Resolvendo Conflitos de Arquivos

Você pode resolver conflitos de duas maneiras:

### Opção A: Antes de sincronizar (Em lote na tela principal)
No cartão **⚠️ Conflitos**, há dois botões de decisão rápida:
- **Todos p/ old/**: Define que todos os arquivos conflitantes do destino serão movidos para `<Destino>/old/` e atualizados com a versão da Origem.
- **Todos Ignorar**: Define que todos os arquivos conflitantes serão mantidos como estão no Destino.

### Opção B: Durante a sincronização (Janela Modal Interativa)
Se houver conflitos pendentes de decisão, o OmniSync exibirá uma janela comparativa lado a lado contendo:
- **Tamanho e Data de Modificação na Origem (Nova)** vs. **no Destino (Atual)**.
- Caixa de seleção **"Aplicar esta escolha para todos os outros conflitos"**.
- Botões **Ignorar (Manter Destino)** e **Substituir (Mover p/ old/)**.

---

## 6. Restaurando Arquivos das Pastas `lixo/` e `old/`

- **Restauração Automática de `lixo/`**:
  - Clique no botão **↩ Restaurar de lixo/** no canto inferior direito da janela principal. Todos os arquivos contidos em `<Destino>/lixo/` serão movidos de volta às suas pastas originais no destino instantaneamente.
- **Inspeção Manual de `lixo/` e `old/`**:
  - Clique em **Abrir destino/lixo/ ↗** ou **Abrir destino/old/ ↗** para abrir a pasta correspondente no gerenciador de arquivos do sistema.
  - Caso um mesmo arquivo seja substituído várias vezes ao longo do tempo, o OmniSync preserva todas as versões anteriores adicionando a data e hora ao nome do arquivo dentro de `old/` (ex.: `relatorio_20261028_114500.xlsx`).

---

## 7. Perguntas Frequentes e Solução de Problemas

**1. As pastas `lixo/` e `old/` entram em loop nas próximas sincronizações?**
Não. O motor de sincronização do OmniSync ignora automaticamente as pastas reservadas `<Destino>/lixo` e `<Destino>/old`, além de pastas internas do sistema operacional (`$RECYCLE.BIN`, `System Volume Information`, `#recycle` de NAS Synology/QNAP e `.Trash`).

**2. O que acontece se a conexão Wi-Fi ou de rede com o NAS oscilar durante a cópia?**
O OmniSync possui política automática de *Retry* (até 3 tentativas com espera progressiva) para cada arquivo. Se um arquivo falhar parcialmente na tentativa 1, os bytes parciais são descontados e a cópia do arquivo é refeita de forma limpa.

**3. Onde ficam registrados eventuais erros técnicos?**
Na aba **📜 Registro de Operações (Log)** dentro do próprio aplicativo e, em caso de falhas graves de ambiente, no arquivo `crash.log` gerado na mesma pasta do executável.
