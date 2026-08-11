# Auditoría de rendimiento — AetherPC (pre-fix)

Fecha: 2026-07-24

## Causas principales de lentitud

| # | Causa | Impacto estimado | Dónde |
|---|--------|------------------|--------|
| 1 | `LibreHardwareMonitor.Computer.Open()` en el camino crítico del snapshot | **2–8+ s** al primer análisis / refrescos | `LibreHardwareSensorService` llamado desde `CaptureSnapshotAsync` |
| 2 | Consultas WMI de seguridad en cada snapshot (`MSFT_MpComputerStatus`, TPM) | **0.5–3 s** | `ReadSecurity` |
| 3 | `Thread.Sleep(80)` en cada lectura de CPU | **80 ms** bloqueantes + presión en UI/timer | `ReadCpu` |
| 4 | `Win32_DiskDrive` completo **por cada volumen** en `GuessMedia` | **N × 100–400 ms** | `ReadDisks` |
| 5 | Snapshot **secuencial** (OS→CPU→RAM→Disk→GPU→Net→Security→Sensors) | Suma de latencias | `WindowsSystemScanner` |
| 6 | Dashboard: timer 2 s + TTL 2 s + `force:true` en “Analizar” | Re-ejecuta el camino pesado | `DashboardViewModel` |
| 7 | Onboarding / full analysis fuerza snapshot completo con sensores | Bloqueo percibido al inicio | `ScanEngine.RunFullAnalysisAsync` |

## Módulos más lentos (orden típico)

1. Sensores (LHM)
2. Seguridad (Defender WMI)
3. Clasificación de discos (WMI repetido)
4. Inventario WMI CPU/GPU/OS (aceptable si se cachea)

## Fallos de detección observados / esperados

| Componente | Problema |
|------------|----------|
| GPU | Solo primera GPU WMI; puede priorizar iGPU o omitir discreta |
| Discos | MediaType = filesystem o “SSD/NVMe” genérico; no usa `MSFT_PhysicalDisk` |
| Placa / BIOS / monitores | **No implementados** |
| RAM tipo/velocidad | Solo primer módulo WMI; tipo numérico crudo |
| Temperaturas | Solo LHM; si falla → vacío sin reintento de otras capas |
| Valores | Algunos defaults (`"CPU"`, `"Unknown"`) pueden parecer inventados |

## Resultados post-fix (este equipo de desarrollo)

Suite de tests `ScannerPerformanceTests` (Release): **OK** en ~5 s total.

| Escaneo | Comportamiento nuevo |
|---------|----------------------|
| **Fast** | Inventario paralelo (Registry/WMI/API), sin LHM ni seguridad pesada, sin `Thread.Sleep`, sin `Win32_DiskDrive` por volumen. Objetivo &lt; 8 s (típicamente mucho menos con caché caliente). |
| **Deep** | Añade sensores (no bloquea si LHM aún calienta), seguridad ligera (servicios+registry), placa/BIOS, monitores, `MSFT_PhysicalDisk`. |
| **Live** | Solo CPU%/RAM vía PerformanceCounter + `GlobalMemoryStatusEx` sobre inventario cacheado (timer UI 1 s). |

## Cambios clave aplicados

1. Escaneo **Fast / Deep / Live** con progreso y cancelación.
2. Caché de inventario 30 min; live TTL ~800 ms.
3. LHM en **warmup background**; el fast path no espera `Computer.Open()`.
4. Detección multi-capa CPU/GPU/RAM/discos/placa/BIOS/monitores/red.
5. Texto canónico **"No detectado por el sistema"** (sin inventar).
6. Dashboard progresivo: rápido → profundo → recomendaciones; métricas live sin congelar UI.

