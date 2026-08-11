# AetherPC — Plan técnico y arquitectura

## 1. Stack elegido

| Capa | Tecnología | Motivo |
|------|------------|--------|
| Lenguaje | C# 12 / .NET 8 LTS | Ecosistema nativo Microsoft, rendimiento, tipado, mantenibilidad |
| UI | **WPF + WPF-UI (Fluent)** | Acceso completo al sistema sin sandbox MSIX; elevación admin fiable; diseño Fluent premium. WinUI 3 queda como evolución futura de la capa visual (misma Application/Infrastructure) |
| DI / Host | `Microsoft.Extensions.Hosting` | Ciclo de vida, logging, configuración estándar |
| MVVM | CommunityToolkit.Mvvm | Código limpio, observable, comandos |
| Datos | SQLite (`Microsoft.Data.Sqlite`) | Local-first, migraciones versionadas, sin servidor |
| Hardware | WMI + Performance Counters + LibreHardwareMonitor | Datos reales; sensores cuando el HW lo permita |
| Gráficos | LiveCharts2 | Series en tiempo real profesionales |
| IA | Motor local de reglas + proveedores opcionales (Ollama / OpenAI / Anthropic / Gemini) | Privacidad por defecto; APIs solo con consentimiento |

### ¿Por qué WPF y no WinUI 3 ahora?

WinUI 3 es la preferencia estética a largo plazo. Para un **optimizador de sistema** (registro, servicios, restore points, elevación UAC, procesos) WPF unpackaged ofrece hoy **mayor estabilidad y compatibilidad** sin restricciones de empaquetado. La arquitectura Clean separa la UI: la capa visual podrá sustituirse por WinUI 3 sin reescribir motores.

## 2. Arquitectura (Clean / SOLID)

```
┌─────────────────────────────────────────────┐
│  AetherPC.App (WPF)                         │  Presentación
│  Views · ViewModels · Themes · Navigation   │
└──────────────────┬──────────────────────────┘
                   │ DI / interfaces
┌──────────────────▼──────────────────────────┐
│  AetherPC.Application                       │  Casos de uso
│  ScanEngine · Profiles · History · Orch.    │
└──────────────────┬──────────────────────────┘
         ┌─────────┼─────────┐
         ▼         ▼         ▼
┌────────────┐ ┌────────┐ ┌────────────┐
│Optimization│ │   AI   │ │  Plugins   │
└──────┬─────┘ └───┬────┘ └─────┬──────┘
       └───────────┼────────────┘
                   ▼
┌─────────────────────────────────────────────┐
│  AetherPC.Infrastructure                    │  Windows / IO
│  WMI · Registry · Processes · SQLite · LHM  │
└─────────────────────────────────────────────┘
                   ▲
┌──────────────────┴──────────────────────────┐
│  AetherPC.Core                              │  Dominio
│  Entities · Enums · Contracts · Events      │
└─────────────────────────────────────────────┘
```

## 3. Flujo obligatorio

**Analizar → Comprender → Recomendar → Preparar (backup) → Optimizar (aprobado) → Verificar**

## 4. Módulos UI

Dashboard · Hardware · Monitor · Análisis · Optimización · Modo Bestia · Limpieza · Gaming · Benchmark · Seguridad · Drivers · IA · Historial · Plugins · Configuración

## 5. Roadmap de implementación

1. Core + contratos + SQLite + escaneo Windows  
2. Shell UI premium + Dashboard en tiempo real  
3. Optimización / Bestia / Limpieza / restauración  
4. Gaming + Benchmarks  
5. Motor IA + chat  
6. Plugins + onboarding + tests + empaquetado  
