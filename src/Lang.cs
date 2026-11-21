// UI translations. Key = English text; columns: en | zh-CN | zh-TW | ja | ko | es | fr | de.
// Missing entries fall back to English. Logs and run summaries stay in English (nvidia-smi compatible).

using System;
using System.Collections.Generic;
using System.Globalization;

namespace GpuS
{
    internal static class Lang
    {
        public static readonly string[] Codes = { "en", "zh-CN", "zh-TW", "ja", "ko", "es", "fr", "de" };
        public static readonly string[] Names = { "English", "简体中文", "繁體中文", "日本語", "한국어", "Español", "Français", "Deutsch" };
        private static int index;
        private static Dictionary<string, string[]> map;

        public static void Init(string setting)
        {
            map = new Dictionary<string, string[]>();
            foreach (string raw in Table.Split('\n'))
            {
                string line = raw.Trim('\r', ' ');
                if (line.Length == 0) continue;
                string[] cols = line.Split('|');
                if (cols.Length > 1) map[cols[0]] = cols;
            }
            index = Resolve(string.IsNullOrEmpty(setting) || setting == "auto" ? CultureInfo.CurrentUICulture.Name : setting);
        }

        private static int Resolve(string name)
        {
            int exact = Array.IndexOf(Codes, name);
            if (exact >= 0) return exact;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return name.Contains("TW") || name.Contains("HK") || name.Contains("MO") || name.Contains("Hant") ? 2 : 1;
            string two = name.Length >= 2 ? name.Substring(0, 2).ToLowerInvariant() : "en";
            int i = Array.IndexOf(Codes, two);
            return i >= 0 ? i : 0;
        }

        public static string L(string en)
        {
            if (index == 0 || en == null) return en;
            string[] t;
            return map.TryGetValue(en, out t) && t.Length > index && t[index].Length > 0 ? t[index] : en;
        }

        public static string F(string format, params object[] args)
        {
            return string.Format(L(format), args);
        }

        private const string Table = @"
Monitor|监控|監控|モニター|모니터|Monitor|Moniteur|Monitor
Processes|进程|處理程序|プロセス|프로세스|Procesos|Processus|Prozesse
Details|详细信息|詳細資訊|詳細|세부 정보|Detalles|Détails|Details
Sensors|传感器|感測器|センサー|센서|Sensores|Capteurs|Sensoren
About|关于|關於|情報|정보|Acerca de|À propos|Info
GPU Monitor|GPU 监控|GPU 監控|GPU モニター|GPU 모니터|Monitor de GPU|Moniteur GPU|GPU-Monitor
Show|显示|顯示|表示|표시|Mostrar|Afficher|Anzeigen
1 min|1 分钟|1 分鐘|1 分|1분|1 min|1 min|1 Min.
10 min|10 分钟|10 分鐘|10 分|10분|10 min|10 min|10 Min.
1 hour|1 小时|1 小時|1 時間|1시간|1 hora|1 heure|1 Stunde
All GPUs|所有 GPU|所有 GPU|すべての GPU|모든 GPU|Todas las GPU|Tous les GPU|Alle GPUs
GPU utilization|GPU 利用率|GPU 使用率|GPU 使用率|GPU 사용률|Uso de GPU|Utilisation GPU|GPU-Auslastung
GPU memory|GPU 显存|GPU 記憶體|GPU メモリ|GPU 메모리|Memoria de GPU|Mémoire GPU|GPU-Speicher
Dedicated GPU memory|专用 GPU 内存|專用 GPU 記憶體|専用 GPU メモリ|전용 GPU 메모리|Memoria de GPU dedicada|Mémoire GPU dédiée|Dedizierter GPU-Speicher
Shared GPU memory|共享 GPU 内存|共用 GPU 記憶體|共有 GPU メモリ|공유 GPU 메모리|Memoria de GPU compartida|Mémoire GPU partagée|Gemeinsamer GPU-Speicher
Temperature|温度|溫度|温度|온도|Temperatura|Température|Temperatur
all engines incl. CUDA|所有引擎（含 CUDA）|所有引擎（含 CUDA）|全エンジン（CUDA 含む）|모든 엔진(CUDA 포함)|todos los motores, incl. CUDA|tous les moteurs, CUDA inclus|alle Engines inkl. CUDA
not throttling|未降频|未降頻|スロットリングなし|스로틀링 없음|sin limitación|pas de bridage|keine Drosselung
Thermal throttling|温度降频|溫度降頻|温度スロットリング|온도 스로틀링|Limitación térmica|Bridage thermique|Thermische Drosselung
HW slowdown|硬件降速|硬體降速|ハードウェア減速|하드웨어 감속|Ralentización HW|Ralentissement matériel|HW-Verlangsamung
Power limit|功耗限制|功耗限制|電力制限|전력 제한|Límite de potencia|Limite de puissance|Leistungslimit
Spilling to system RAM|溢出到系统内存|溢出到系統記憶體|システム RAM へあふれ中|시스템 RAM으로 넘침|Desbordando a la RAM|Débordement vers la RAM|Auslagerung in System-RAM
Not available (NVIDIA only)|不可用（仅限 NVIDIA）|不可用（僅限 NVIDIA）|利用不可（NVIDIA のみ）|사용 불가(NVIDIA 전용)|No disponible (solo NVIDIA)|Non disponible (NVIDIA uniquement)|Nicht verfügbar (nur NVIDIA)
{0:0} seconds|{0:0} 秒|{0:0} 秒|{0:0} 秒|{0:0}초|{0:0} segundos|{0:0} secondes|{0:0} Sekunden
{0:0} minutes|{0:0} 分钟|{0:0} 分鐘|{0:0} 分|{0:0}분|{0:0} minutos|{0:0} minutes|{0:0} Minuten
{0:N0} MB used  ·  {1:N0} MB free  ·  {2:0.0}%|已用 {0:N0} MB  ·  可用 {1:N0} MB  ·  {2:0.0}%|已用 {0:N0} MB  ·  可用 {1:N0} MB  ·  {2:0.0}%|使用 {0:N0} MB  ·  空き {1:N0} MB  ·  {2:0.0}%|사용 {0:N0} MB  ·  여유 {1:N0} MB  ·  {2:0.0}%|{0:N0} MB usados  ·  {1:N0} MB libres  ·  {2:0.0} %|{0:N0} Mo utilisés  ·  {1:N0} Mo libres  ·  {2:0.0} %|{0:N0} MB belegt  ·  {1:N0} MB frei  ·  {2:0.0} %
shared {0:N0} MB|共享 {0:N0} MB|共用 {0:N0} MB|共有 {0:N0} MB|공유 {0:N0} MB|compartida {0:N0} MB|partagée {0:N0} Mo|gemeinsam {0:N0} MB
{0:N0} MB used|已用 {0:N0} MB|已用 {0:N0} MB|使用 {0:N0} MB|사용 {0:N0} MB|{0:N0} MB usados|{0:N0} Mo utilisés|{0:N0} MB belegt
System RAM|系统内存|系統記憶體|システムメモリ|시스템 메모리|RAM del sistema|RAM système|System-RAM
CPU Usage|CPU 利用率|CPU 使用率|CPU 使用率|CPU 사용률|Uso de CPU|Utilisation CPU|CPU-Auslastung
Network|网络|網路|ネットワーク|네트워크|Red|Réseau|Netzwerk
RAM Used|已用内存|已用記憶體|使用メモリ (RAM)|사용 RAM|RAM usada|RAM utilisée|Belegter RAM
Utilization|利用率|使用率|使用率|사용률|Uso|Utilisation|Auslastung
Memory|显存|記憶體|メモリ|메모리|Memoria|Mémoire|Speicher
GPU clock|GPU 频率|GPU 時脈|GPU クロック|GPU 클럭|Reloj de GPU|Fréquence GPU|GPU-Takt
Power|功耗|功耗|電力|전력|Potencia|Puissance|Leistung
Fan|风扇|風扇|ファン|팬|Ventilador|Ventilateur|Lüfter
PROCESS|进程|處理程序|プロセス|프로세스|PROCESO|PROCESSUS|PROZESS
DEDICATED|专用|專用|専用|전용|DEDICADA|DÉDIÉE|DEDIZIERT
SHARED|共享|共用|共有|공유|COMPARTIDA|PARTAGÉE|GEMEINSAM
No process is using this GPU|没有进程在使用此 GPU|沒有處理程序正在使用此 GPU|この GPU を使用しているプロセスはありません|이 GPU를 사용하는 프로세스가 없습니다|Ningún proceso usa esta GPU|Aucun processus n'utilise ce GPU|Kein Prozess verwendet diese GPU
Collecting…|正在收集…|正在收集…|収集中…|수집 중…|Recopilando…|Collecte…|Wird erfasst…
GPU % covers graphics and video work only: Windows does not report CUDA compute per process (WDDM driver mode). Memory is exact; the total GPU load is on Monitor.|GPU % 仅包含图形和视频负载：Windows（WDDM 驱动模式）不按进程报告 CUDA 计算。显存数值准确；GPU 总负载见“监控”。|GPU % 僅包含圖形與視訊負載：Windows（WDDM 驅動模式）不按處理程序回報 CUDA 運算。記憶體數值準確；GPU 總負載請見「監控」。|GPU % はグラフィックスとビデオ処理のみです。Windows（WDDM ドライバー モード）はプロセスごとの CUDA 計算を報告しません。メモリは正確です。GPU 全体の負荷は「モニター」で確認できます。|GPU %는 그래픽/비디오 작업만 포함합니다. Windows(WDDM 드라이버 모드)는 프로세스별 CUDA 연산을 보고하지 않습니다. 메모리는 정확하며 전체 GPU 부하는 '모니터'에서 확인하세요.|El % de GPU solo cubre gráficos y vídeo: Windows (modo WDDM) no informa del cómputo CUDA por proceso. La memoria es exacta; la carga total está en Monitor.|Le % GPU ne couvre que le graphisme et la vidéo : Windows (mode WDDM) ne signale pas le calcul CUDA par processus. La mémoire est exacte ; la charge totale est dans Moniteur.|GPU-% umfasst nur Grafik- und Videolast: Windows (WDDM-Modus) meldet CUDA-Berechnungen nicht pro Prozess. Speicherwerte sind exakt; die Gesamtlast steht unter Monitor.
Graphics Processor|图形处理器|圖形處理器|グラフィックス プロセッサ|그래픽 프로세서|Procesador gráfico|Processeur graphique|Grafikprozessor
Memory & Bus|显存与总线|記憶體與匯流排|メモリとバス|메모리 및 버스|Memoria y bus|Mémoire et bus|Speicher & Bus
Clocks & Power|频率与功耗|時脈與功耗|クロックと電力|클럭 및 전력|Relojes y potencia|Fréquences et puissance|Takt & Leistung
Driver|驱动程序|驅動程式|ドライバー|드라이버|Controlador|Pilote|Treiber
Graphics & Compute APIs|图形与计算 API|圖形與運算 API|グラフィックス/計算 API|그래픽 및 컴퓨팅 API|API de gráficos y cómputo|API graphiques et de calcul|Grafik- & Compute-APIs
Name|名称|名稱|名前|이름|Nombre|Nom|Name
Architecture|架构|架構|アーキテクチャ|아키텍처|Arquitectura|Architecture|Architektur
Shaders|着色器|著色器|シェーダー|셰이더|Sombreadores|Shaders|Shader
Vendor|厂商|廠商|ベンダー|제조사|Fabricante|Fabricant|Hersteller
Subvendor|子厂商|子廠商|サブベンダー|하위 제조사|Subfabricante|Sous-fabricant|Subhersteller
Device ID|设备 ID|裝置 ID|デバイス ID|장치 ID|ID de dispositivo|ID du périphérique|Geräte-ID
Revision|修订版本|修訂版本|リビジョン|리비전|Revisión|Révision|Revision
BIOS Version|BIOS 版本|BIOS 版本|BIOS バージョン|BIOS 버전|Versión de BIOS|Version du BIOS|BIOS-Version
Memory Size|显存容量|記憶體容量|メモリ容量|메모리 크기|Tamaño de memoria|Taille mémoire|Speichergröße
Memory Bus Width|显存位宽|記憶體匯流排寬度|メモリバス幅|메모리 버스 폭|Ancho de bus de memoria|Largeur du bus mémoire|Speicherbusbreite
Shared System Memory|共享系统内存|共用系統記憶體|共有システムメモリ|공유 시스템 메모리|Memoria del sistema compartida|Mémoire système partagée|Gemeinsamer Systemspeicher
Bus Interface|总线接口|匯流排介面|バスインターフェイス|버스 인터페이스|Interfaz de bus|Interface de bus|Busschnittstelle
Location|位置|位置|場所|위치|Ubicación|Emplacement|Ort
GPU Clock (max)|GPU 频率（最大）|GPU 時脈（最大）|GPU クロック（最大）|GPU 클럭(최대)|Reloj de GPU (máx.)|Fréquence GPU (max)|GPU-Takt (max.)
Memory Clock (max)|显存频率（最大）|記憶體時脈（最大）|メモリクロック（最大）|메모리 클럭(최대)|Reloj de memoria (máx.)|Fréquence mémoire (max)|Speichertakt (max.)
Power Limit|功耗上限|功耗上限|電力上限|전력 한도|Límite de potencia|Limite de puissance|Leistungsgrenze
Driver Version|驱动版本|驅動程式版本|ドライバーのバージョン|드라이버 버전|Versión del controlador|Version du pilote|Treiberversion
Driver Date|驱动日期|驅動程式日期|ドライバーの日付|드라이버 날짜|Fecha del controlador|Date du pilote|Treiberdatum
Driver Provider|驱动提供商|驅動程式提供者|ドライバーの提供元|드라이버 공급자|Proveedor del controlador|Fournisseur du pilote|Treiberanbieter
NVIDIA Driver|NVIDIA 驱动|NVIDIA 驅動程式|NVIDIA ドライバー|NVIDIA 드라이버|Controlador NVIDIA|Pilote NVIDIA|NVIDIA-Treiber
N/A|无|無|なし|없음|N/D|N/D|k. A.
Not supported|不支持|不支援|非対応|지원 안 함|No compatible|Non pris en charge|Nicht unterstützt
Detecting…|正在检测…|正在偵測…|検出中…|감지 중…|Detectando…|Détection…|Wird erkannt…
CUDA cores|CUDA 核心|CUDA 核心|CUDA コア|CUDA 코어|núcleos CUDA|cœurs CUDA|CUDA-Kerne
running at|当前|目前|現在|현재|funcionando a|actuellement|aktuell
N/A (integrated or not reported)|无（集成显卡或未报告）|無（內建顯示卡或未回報）|なし（内蔵または未報告）|없음(내장 또는 미보고)|N/D (integrada o no informada)|N/D (intégré ou non signalé)|k. A. (integriert oder nicht gemeldet)
Refresh|刷新|重新整理|更新間隔|새로 고침|Actualizar|Actualisation|Aktualisierung
Reset Min/Max|重置最小/最大|重設最小/最大|最小/最大をリセット|최소/최대 초기화|Restablecer mín./máx.|Réinitialiser min/max|Min/Max zurücksetzen
Log to CSV|记录到 CSV|記錄到 CSV|CSV に記録|CSV로 기록|Registrar en CSV|Journaliser en CSV|In CSV protokollieren
Stop logging|停止记录|停止記錄|記録を停止|기록 중지|Detener registro|Arrêter la journalisation|Protokollierung beenden
SENSOR|传感器|感測器|センサー|센서|SENSOR|CAPTEUR|SENSOR
CURRENT|当前|目前|現在|현재|ACTUAL|ACTUEL|AKTUELL
MINIMUM|最小|最小|最小|최소|MÍNIMO|MINIMUM|MINIMUM
MAXIMUM|最大|最大|最大|최대|MÁXIMO|MAXIMUM|MAXIMUM
AVERAGE|平均|平均|平均|평균|PROMEDIO|MOYENNE|DURCHSCHNITT
HISTORY|历史|歷史|履歴|기록|HISTORIAL|HISTORIQUE|VERLAUF
Waiting for data…|等待数据…|等待資料…|データ待機中…|데이터 대기 중…|Esperando datos…|En attente de données…|Warte auf Daten…
GPU Clock|GPU 频率|GPU 時脈|GPU クロック|GPU 클럭|Reloj de GPU|Fréquence GPU|GPU-Takt
Memory Clock|显存频率|記憶體時脈|メモリクロック|메모리 클럭|Reloj de memoria|Fréquence mémoire|Speichertakt
Video Clock|视频频率|視訊時脈|ビデオクロック|비디오 클럭|Reloj de vídeo|Fréquence vidéo|Videotakt
GPU Temperature|GPU 温度|GPU 溫度|GPU 温度|GPU 온도|Temperatura de GPU|Température GPU|GPU-Temperatur
Fan Speed|风扇转速|風扇轉速|ファン速度|팬 속도|Velocidad del ventilador|Vitesse du ventilateur|Lüfterdrehzahl
Board Power Draw|板卡功耗|板卡功耗|ボード消費電力|보드 전력 소비|Consumo de la tarjeta|Consommation de la carte|Leistungsaufnahme
Power (% of limit)|功耗（占上限 %）|功耗（佔上限 %）|電力（上限比 %）|전력(한도 대비 %)|Potencia (% del límite)|Puissance (% de la limite)|Leistung (% des Limits)
GPU Load|GPU 负载|GPU 負載|GPU 負荷|GPU 부하|Carga de GPU|Charge GPU|GPU-Last
Memory Controller Load|显存控制器负载|記憶體控制器負載|メモリコントローラー負荷|메모리 컨트롤러 부하|Carga del controlador de memoria|Charge du contrôleur mémoire|Speichercontroller-Last
Video Encoder Load|视频编码负载|視訊編碼負載|ビデオエンコーダー負荷|비디오 인코더 부하|Carga del codificador|Charge de l'encodeur|Encoder-Last
Video Decoder Load|视频解码负载|視訊解碼負載|ビデオデコーダー負荷|비디오 디코더 부하|Carga del decodificador|Charge du décodeur|Decoder-Last
Memory Used|已用显存|已用記憶體|使用メモリ|사용 메모리|Memoria usada|Mémoire utilisée|Belegter Speicher
Memory Free|可用显存|可用記憶體|空きメモリ|여유 메모리|Memoria libre|Mémoire libre|Freier Speicher
Memory Used (%)|已用显存 (%)|已用記憶體 (%)|使用メモリ (%)|사용 메모리(%)|Memoria usada (%)|Mémoire utilisée (%)|Belegter Speicher (%)
Shared Memory Used|已用共享内存|已用共用記憶體|使用共有メモリ|사용 공유 메모리|Memoria compartida usada|Mémoire partagée utilisée|Belegter gemeinsamer Speicher
Performance State|性能状态|效能狀態|パフォーマンス状態|성능 상태|Estado de rendimiento|État de performance|Leistungszustand
PerfCap Reason|性能限制原因|效能限制原因|性能制限の理由|성능 제한 원인|Motivo de limitación|Raison de limitation|Drosselungsgrund
3D Engine|3D 引擎|3D 引擎|3D エンジン|3D 엔진|Motor 3D|Moteur 3D|3D-Engine
Compute Engine|计算引擎|運算引擎|計算エンジン|컴퓨팅 엔진|Motor de cómputo|Moteur de calcul|Compute-Engine
Copy Engine|复制引擎|複製引擎|コピーエンジン|복사 엔진|Motor de copia|Moteur de copie|Kopier-Engine
Video Decode|视频解码|視訊解碼|ビデオデコード|비디오 디코드|Decodificación de vídeo|Décodage vidéo|Videodekodierung
Video Encode|视频编码|視訊編碼|ビデオエンコード|비디오 인코드|Codificación de vídeo|Encodage vidéo|Videokodierung
Video Processing|视频处理|視訊處理|ビデオ処理|비디오 처리|Procesamiento de vídeo|Traitement vidéo|Videoverarbeitung
Memory Used (Dedicated)|已用专用内存|已用專用記憶體|使用専用メモリ|사용 전용 메모리|Memoria dedicada usada|Mémoire dédiée utilisée|Belegter dedizierter Speicher
Memory Used (Shared)|已用共享内存|已用共用記憶體|使用共有メモリ|사용 공유 메모리|Memoria compartida usada|Mémoire partagée utilisée|Belegter gemeinsamer Speicher
Mini|迷你窗口|迷你視窗|ミニ|미니|Mini|Mini|Mini
Settings|设置|設定|設定|설정|Configuración|Paramètres|Einstellungen
Report|报告|報告|レポート|보고서|Informe|Rapport|Bericht
Copy report to clipboard|复制报告到剪贴板|複製報告到剪貼簿|レポートをクリップボードにコピー|보고서를 클립보드에 복사|Copiar informe al portapapeles|Copier le rapport|Bericht in Zwischenablage kopieren
Save report…|保存报告…|儲存報告…|レポートを保存…|보고서 저장…|Guardar informe…|Enregistrer le rapport…|Bericht speichern…
Copy run summary (Markdown)|复制运行摘要（Markdown）|複製執行摘要（Markdown）|実行サマリーをコピー（Markdown）|실행 요약 복사(Markdown)|Copiar resumen de ejecución (Markdown)|Copier le résumé d'exécution (Markdown)|Laufzusammenfassung kopieren (Markdown)
Standard user — no admin rights needed|标准用户——无需管理员权限|標準使用者——無需系統管理員權限|標準ユーザー — 管理者権限は不要|표준 사용자 — 관리자 권한 불필요|Usuario estándar: sin permisos de administrador|Utilisateur standard — aucun droit admin requis|Standardbenutzer – keine Adminrechte nötig
Running as administrator|以管理员身份运行|以系統管理員身分執行|管理者として実行中|관리자로 실행 중|Ejecutando como administrador|Exécution en tant qu'administrateur|Als Administrator ausgeführt
{0} GPU(s)|{0} 个 GPU|{0} 個 GPU|GPU {0} 基|GPU {0}개|{0} GPU|{0} GPU|{0} GPU(s)
NVML sensors|NVML 传感器|NVML 感測器|NVML センサー|NVML 센서|Sensores NVML|Capteurs NVML|NVML-Sensoren
Windows performance counters|Windows 性能计数器|Windows 效能計數器|Windows パフォーマンス カウンター|Windows 성능 카운터|Contadores de rendimiento de Windows|Compteurs de performance Windows|Windows-Leistungsindikatoren
Detecting graphics adapters…|正在检测显卡…|正在偵測顯示卡…|グラフィックス アダプターを検出中…|그래픽 어댑터 감지 중…|Detectando adaptadores gráficos…|Détection des cartes graphiques…|Grafikkarten werden erkannt…
No graphics adapter found|未找到显卡|找不到顯示卡|グラフィックス アダプターが見つかりません|그래픽 어댑터를 찾을 수 없음|No se encontró adaptador gráfico|Aucune carte graphique trouvée|Keine Grafikkarte gefunden
Copied to the clipboard|已复制到剪贴板|已複製到剪貼簿|クリップボードにコピーしました|클립보드에 복사됨|Copiado al portapapeles|Copié dans le presse-papiers|In die Zwischenablage kopiert
Saved: {0}|已保存：{0}|已儲存：{0}|保存しました: {0}|저장됨: {0}|Guardado: {0}|Enregistré : {0}|Gespeichert: {0}
Logging to {0}|正在记录到 {0}|正在記錄到 {0}|{0} に記録中|{0}에 기록 중|Registrando en {0}|Journalisation vers {0}|Protokolliere nach {0}
Settings saved|设置已保存|設定已儲存|設定を保存しました|설정이 저장됨|Configuración guardada|Paramètres enregistrés|Einstellungen gespeichert
Show GPU-S|显示 GPU-S|顯示 GPU-S|GPU-S を表示|GPU-S 표시|Mostrar GPU-S|Afficher GPU-S|GPU-S anzeigen
Mini window|迷你窗口|迷你視窗|ミニウィンドウ|미니 창|Ventana mini|Mini-fenêtre|Minifenster
Exit|退出|結束|終了|종료|Salir|Quitter|Beenden
GPU temperature is high|GPU 温度过高|GPU 溫度過高|GPU 温度が高くなっています|GPU 온도가 높습니다|Temperatura de GPU alta|Température GPU élevée|GPU-Temperatur ist hoch
GPU memory is almost full|GPU 显存即将用尽|GPU 記憶體即將用盡|GPU メモリがほぼいっぱいです|GPU 메모리가 거의 가득 찼습니다|Memoria de GPU casi llena|Mémoire GPU presque pleine|GPU-Speicher fast voll
GPU memory is spilling into system RAM|GPU 显存正在溢出到系统内存|GPU 記憶體正在溢出到系統記憶體|GPU メモリがシステム RAM にあふれています|GPU 메모리가 시스템 RAM으로 넘치고 있습니다|La memoria de GPU se desborda a la RAM|La mémoire GPU déborde dans la RAM|GPU-Speicher wird in den System-RAM ausgelagert
{0} is at {1:0} °C (alert at {2} °C).|{0} 当前 {1:0} °C（警报阈值 {2} °C）。|{0} 目前 {1:0} °C（警示門檻 {2} °C）。|{0} は {1:0} °C です（警告しきい値 {2} °C）。|{0}: {1:0} °C (경고 기준 {2} °C).|{0} está a {1:0} °C (alerta a {2} °C).|{0} est à {1:0} °C (alerte à {2} °C).|{0} hat {1:0} °C (Alarm bei {2} °C).
{0}: {1:N0} of {2:N0} MB used ({3:0}%).|{0}：已用 {1:N0} / {2:N0} MB（{3:0}%）。|{0}：已用 {1:N0} / {2:N0} MB（{3:0}%）。|{0}: {2:N0} MB 中 {1:N0} MB 使用（{3:0}%）。|{0}: {2:N0} MB 중 {1:N0} MB 사용({3:0}%).|{0}: {1:N0} de {2:N0} MB usados ({3:0} %).|{0} : {1:N0} sur {2:N0} Mo utilisés ({3:0} %).|{0}: {1:N0} von {2:N0} MB belegt ({3:0} %).
{0}: about {1:N0} MB moved to shared system memory, so training may slow down a lot. Reduce the batch size, or set 'CUDA - Sysmem Fallback Policy' to 'Prefer No Sysmem Fallback' in the NVIDIA Control Panel.|{0}：约 {1:N0} MB 已移至共享系统内存，训练可能明显变慢。请减小批大小，或在 NVIDIA 控制面板中将 'CUDA - Sysmem Fallback Policy' 设为 'Prefer No Sysmem Fallback'。|{0}：約 {1:N0} MB 已移至共用系統記憶體，訓練可能明顯變慢。請減少批次大小，或在 NVIDIA 控制面板將 'CUDA - Sysmem Fallback Policy' 設為 'Prefer No Sysmem Fallback'。|{0}: 約 {1:N0} MB が共有システムメモリに移動したため、学習が大幅に遅くなる可能性があります。バッチサイズを減らすか、NVIDIA コントロール パネルで 'CUDA - Sysmem Fallback Policy' を 'Prefer No Sysmem Fallback' に設定してください。|{0}: 약 {1:N0} MB가 공유 시스템 메모리로 이동하여 학습이 크게 느려질 수 있습니다. 배치 크기를 줄이거나 NVIDIA 제어판에서 'CUDA - Sysmem Fallback Policy'를 'Prefer No Sysmem Fallback'으로 설정하세요.|{0}: unos {1:N0} MB se movieron a la memoria compartida del sistema y el entrenamiento puede ralentizarse mucho. Reduzca el tamaño de lote o ajuste 'CUDA - Sysmem Fallback Policy' a 'Prefer No Sysmem Fallback' en el Panel de control de NVIDIA.|{0} : environ {1:N0} Mo déplacés vers la mémoire système partagée ; l'entraînement peut fortement ralentir. Réduisez la taille de lot ou réglez 'CUDA - Sysmem Fallback Policy' sur 'Prefer No Sysmem Fallback' dans le Panneau de configuration NVIDIA.|{0}: etwa {1:N0} MB wurden in den gemeinsamen Systemspeicher verschoben; das Training kann stark verlangsamen. Batchgröße verringern oder in der NVIDIA-Systemsteuerung 'CUDA - Sysmem Fallback Policy' auf 'Prefer No Sysmem Fallback' setzen.
Language|语言|語言|言語|언어|Idioma|Langue|Sprache
Auto (system)|自动（系统）|自動（系統）|自動（システム）|자동(시스템)|Automático (sistema)|Automatique (système)|Automatisch (System)
Minimize to the notification area (tray)|最小化到通知区域（托盘）|最小化到通知區域（系統匣）|通知領域（トレイ）に最小化|알림 영역(트레이)으로 최소화|Minimizar al área de notificación|Réduire dans la zone de notification|In den Infobereich minimieren
Alert when GPU temperature reaches|GPU 温度达到此值时提醒|GPU 溫度達到此值時提醒|GPU 温度がこの値に達したら通知|GPU 온도가 다음 값에 도달하면 알림|Avisar cuando la temperatura de GPU llegue a|Alerter quand la température GPU atteint|Warnen bei GPU-Temperatur ab
Alert when GPU memory use reaches|GPU 显存使用达到此值时提醒|GPU 記憶體使用達到此值時提醒|GPU メモリ使用率がこの値に達したら通知|GPU 메모리 사용량이 다음 값에 도달하면 알림|Avisar cuando la memoria de GPU llegue a|Alerter quand la mémoire GPU atteint|Warnen bei GPU-Speicher ab
Alert when GPU memory spills into system RAM|GPU 显存溢出到系统内存时提醒|GPU 記憶體溢出到系統記憶體時提醒|GPU メモリがシステム RAM にあふれたら通知|GPU 메모리가 시스템 RAM으로 넘치면 알림|Avisar si la memoria de GPU se desborda a la RAM|Alerter en cas de débordement vers la RAM système|Warnen bei Auslagerung in den System-RAM
Log values without units (like nvidia-smi --format=csv,nounits)|记录不带单位的数值（同 nvidia-smi --format=csv,nounits）|記錄不帶單位的數值（同 nvidia-smi --format=csv,nounits）|単位なしで記録（nvidia-smi --format=csv,nounits と同じ）|단위 없이 기록(nvidia-smi --format=csv,nounits와 동일)|Registrar sin unidades (como nvidia-smi --format=csv,nounits)|Journaliser sans unités (comme nvidia-smi --format=csv,nounits)|Ohne Einheiten protokollieren (wie nvidia-smi --format=csv,nounits)
OK|确定|確定|OK|확인|Aceptar|OK|OK
Cancel|取消|取消|キャンセル|취소|Cancelar|Annuler|Abbrechen
Restart GPU-S now to change the language?|立即重启 GPU-S 以切换语言？|立即重新啟動 GPU-S 以切換語言？|言語を変更するために GPU-S を再起動しますか？|언어를 변경하려면 지금 GPU-S를 다시 시작할까요?|¿Reiniciar GPU-S ahora para cambiar el idioma?|Redémarrer GPU-S maintenant pour changer de langue ?|GPU-S jetzt neu starten, um die Sprache zu ändern?
A small GPU monitor for Windows — one file, no installation, no administrator rights.|轻量级 Windows GPU 监控工具——单个文件，无需安装，无需管理员权限。|輕量級 Windows GPU 監控工具——單一檔案，免安裝，無需系統管理員權限。|Windows 用の小さな GPU モニター — 1 ファイル、インストール不要、管理者権限不要。|Windows용 소형 GPU 모니터 — 단일 파일, 설치 불필요, 관리자 권한 불필요.|Un pequeño monitor de GPU para Windows: un solo archivo, sin instalación ni permisos de administrador.|Un petit moniteur GPU pour Windows — un seul fichier, sans installation ni droits administrateur.|Ein kleiner GPU-Monitor für Windows – eine Datei, keine Installation, keine Adminrechte.
License|许可|授權|ライセンス|라이선스|Licencia|Licence|Lizenz
Free · no ads · no tracking · nothing is sent over the network|免费 · 无广告 · 无跟踪 · 不发送任何网络数据|免費 · 無廣告 · 無追蹤 · 不傳送任何網路資料|無料 · 広告なし · トラッキングなし · ネットワーク通信なし|무료 · 광고 없음 · 추적 없음 · 네트워크 전송 없음|Gratis · sin anuncios · sin rastreo · no envía nada por la red|Gratuit · sans publicité · sans pistage · aucune donnée envoyée|Kostenlos · keine Werbung · kein Tracking · keine Netzwerkübertragung
Author|作者|作者|作者|작성자|Autor|Auteur|Autor
Email|电子邮件|電子郵件|メール|이메일|Correo|E-mail|E-Mail
Data sources|数据来源|資料來源|データソース|데이터 소스|Fuentes de datos|Sources de données|Datenquellen
Runtime|运行环境|執行環境|ランタイム|런타임|Entorno|Environnement|Laufzeit
driver|驱动|驅動程式|ドライバー|드라이버|controlador|pilote|Treiber
";
    }
}
