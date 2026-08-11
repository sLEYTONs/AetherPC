using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AetherPC.Core.Localization;
using Microsoft.Win32;

namespace AetherPC.Infrastructure.Windows;

/// <summary>
/// Nombres y descripciones de servicios según el idioma de la APP, no el de Windows.
/// WMI/MUI cubren el caso con language pack; el catálogo cubre Windows en inglés + app en español (y al revés).
/// </summary>
internal static class ServiceDisplayCatalog
{
    private const uint MuiLanguageName = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetThreadPreferredUILanguages(uint dwFlags, string? pwszLanguagesBuffer, ref uint pulNumLanguages);

    [DllImport("kernel32.dll")]
    private static extern ushort SetThreadUILanguage(ushort langId);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    public static void PushAppUiLanguage()
    {
        try
        {
            var culture = Loc.IsEnglish ? new CultureInfo("en-US") : new CultureInfo("es-ES");
            CultureInfo.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            uint n = 0;
            var buf = (Loc.IsEnglish ? "en-US" : "es-ES") + "\0\0";
            SetThreadPreferredUILanguages(MuiLanguageName, buf, ref n);
            SetThreadUILanguage((ushort)(Loc.IsEnglish ? 0x0409 : 0x0C0A));
        }
        catch { /* */ }
    }

    public static void ClearPreferredUiLanguages()
    {
        try
        {
            uint n = 0;
            SetThreadPreferredUILanguages(MuiLanguageName, "\0", ref n);
        }
        catch { /* */ }
    }

    public static (string DisplayName, string? Description) Apply(
        string serviceName, string displayName, string? description)
    {
        var key = CatalogKey(serviceName);
        if (Map.TryGetValue(key, out var t))
        {
            return Loc.IsEnglish
                ? (t.EnName, t.EnDesc)
                : (t.EsName, t.EsDesc);
        }

        var muiName = ResolveRegistryMui(serviceName, "DisplayName");
        var muiDesc = ResolveRegistryMui(serviceName, "Description");
        var dn = FirstNonEmpty(muiName, displayName, serviceName);
        var desc = FirstNonEmpty(muiDesc, description);
        return (dn, desc);
    }

    private static string CatalogKey(string serviceName)
    {
        var i = serviceName.IndexOf('_');
        return i > 0 ? serviceName[..i] : serviceName;
    }

    private static string FirstNonEmpty(params string?[] parts)
    {
        foreach (var p in parts)
        {
            if (!string.IsNullOrWhiteSpace(p))
                return p.Trim();
        }
        return "";
    }

    private static string? ResolveRegistryMui(string serviceName, string valueName)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
            if (k is null) return null;
            var raw = k.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim();
            if (!raw.StartsWith('@')) return null;

            var expanded = Environment.ExpandEnvironmentVariables(raw);
            var sb = new StringBuilder(1024);
            if (SHLoadIndirectString(expanded, sb, sb.Capacity, IntPtr.Zero) == 0)
            {
                var s = sb.ToString().Trim();
                if (s.Length > 0) return s;
            }

            // Forzar .mui del idioma de la app si el language pack está instalado.
            var forced = ForceMuiPath(expanded);
            if (forced is not null && !string.Equals(forced, expanded, StringComparison.OrdinalIgnoreCase))
            {
                sb.Clear();
                if (SHLoadIndirectString(forced, sb, sb.Capacity, IntPtr.Zero) == 0)
                {
                    var s = sb.ToString().Trim();
                    if (s.Length > 0) return s;
                }
            }
        }
        catch { /* */ }

        return null;
    }

    private static string? ForceMuiPath(string indirect)
    {
        // @C:\Windows\System32\foo.dll,-101  →  @C:\Windows\System32\es-ES\foo.dll.mui,-101
        if (!indirect.StartsWith('@')) return null;
        var comma = indirect.LastIndexOf(',');
        if (comma < 0) return null;
        var path = indirect[1..comma];
        var suffix = indirect[comma..];
        var dir = Path.GetDirectoryName(path);
        var file = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return null;
        var tag = Loc.IsEnglish ? "en-US" : "es-ES";
        var mui = Path.Combine(dir, tag, file + ".mui");
        return File.Exists(mui) ? "@" + mui + suffix : null;
    }

    private readonly record struct Entry(string EsName, string EnName, string EsDesc, string EnDesc);

    private static readonly Dictionary<string, Entry> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WSearch"] = new("Búsqueda de Windows", "Windows Search",
            "Proporciona indexación de contenido y resultados de búsqueda en el equipo.",
            "Provides content indexing and search results on this PC."),
        ["SysMain"] = new("SysMain", "SysMain",
            "Mantiene y mejora el rendimiento del sistema con el tiempo.",
            "Maintains and improves system performance over time."),
        ["Spooler"] = new("Cola de impresión", "Print Spooler",
            "Carga archivos en memoria para imprimirlos más tarde.",
            "Loads files into memory for later printing."),
        ["PrintNotify"] = new("Extensiones y notificaciones de impresora", "Printer Extensions and Notifications",
            "Abre cuadros de diálogo personalizados de impresora y notificaciones.",
            "Opens custom printer dialogs and handles notifications."),
        ["wuauserv"] = new("Windows Update", "Windows Update",
            "Detecta, descarga e instala actualizaciones de Windows y otros programas.",
            "Detects, downloads, and installs Windows and other program updates."),
        ["UsoSvc"] = new("Servicio de orquestador de actualizaciones", "Update Orchestrator Service",
            "Administra las descargas e instalaciones de Windows Update.",
            "Manages Windows Update downloads and installations."),
        ["WaaSMedicSvc"] = new("Servicio de medicación de Windows Update", "Windows Update Medic Service",
            "Habilita la reparación de componentes de Windows Update.",
            "Enables remediation of Windows Update components."),
        ["bits"] = new("Servicio de transferencia inteligente en segundo plano", "Background Intelligent Transfer Service",
            "Transfiere archivos en segundo plano usando el ancho de banda inactivo.",
            "Transfers files in the background using idle network bandwidth."),
        ["DoSvc"] = new("Optimización de distribución", "Delivery Optimization",
            "Realiza descargas en segundo plano y comparte actualizaciones en la red.",
            "Performs background downloads and may share updates on the local network."),
        ["DiagTrack"] = new("Experiencias del usuario conectado y telemetría", "Connected User Experiences and Telemetry",
            "Habilita funciones de experiencia y telemetría de diagnóstico.",
            "Enables experiences and diagnostic telemetry features."),
        ["dmwappushservice"] = new("Servicio de enrutamiento de mensajes WAP push de administración de dispositivos", "Device Management Wireless Application Protocol (WAP) Push message Routing Service",
            "Enruta mensajes WAP push usados en la administración de dispositivos.",
            "Routes WAP push messages used for device management."),
        ["DPS"] = new("Servicio de directivas de diagnóstico", "Diagnostic Policy Service",
            "Detecta, soluciona y resuelve problemas de componentes de Windows.",
            "Detects, troubleshoots, and resolves problems in Windows components."),
        ["PcaSvc"] = new("Servicio del Asistente para compatibilidad de programas", "Program Compatibility Assistant Service",
            "Proporciona compatibilidad para programas al ejecutar software más antiguo.",
            "Provides compatibility support when running older software."),
        ["WerSvc"] = new("Servicio de informes de errores de Windows", "Windows Error Reporting Service",
            "Permite enviar informes de errores cuando un programa deja de funcionar.",
            "Allows error reporting when a program stops working or responding."),
        ["WdiServiceHost"] = new("Host del servicio de diagnósticos", "Diagnostic Service Host",
            "Hospeda diagnósticos que se deben ejecutar en un contexto de servicio local.",
            "Hosts diagnostics that must run in a Local Service context."),
        ["WdiSystemHost"] = new("Host del sistema de diagnósticos", "Diagnostic System Host",
            "Hospeda diagnósticos que se deben ejecutar en un contexto de sistema local.",
            "Hosts diagnostics that must run in a Local System context."),
        ["Fax"] = new("Fax", "Fax",
            "Permite enviar y recibir faxes.",
            "Enables you to send and receive faxes."),
        ["RemoteRegistry"] = new("Registro remoto", "Remote Registry",
            "Permite a usuarios remotos modificar la configuración del Registro.",
            "Enables remote users to modify registry settings on this computer."),
        ["TabletInputService"] = new("Servicio de teclado táctil y panel de escritura a mano", "Touch Keyboard and Handwriting Panel Service",
            "Habilita la funcionalidad del teclado táctil y del panel de escritura.",
            "Enables Touch Keyboard and Handwriting Panel functionality."),
        ["TrkWks"] = new("Cliente de seguimiento de vínculos distribuidos", "Distributed Link Tracking Client",
            "Mantiene vínculos entre archivos NTFS de un equipo o de una red.",
            "Maintains links between NTFS files within a computer or across a network."),
        ["WbioSrvc"] = new("Servicio biométrico de Windows", "Windows Biometric Service",
            "Captura, compara, manipula y almacena datos biométricos.",
            "Captures, compares, manipulates, and stores biometric data."),
        ["WMPNetworkSvc"] = new("Servicio de uso compartido de red del Reproductor de Windows Media", "Windows Media Player Network Sharing Service",
            "Comparte bibliotecas del Reproductor de Windows Media con otros dispositivos.",
            "Shares Windows Media Player libraries with other networked players and media devices."),
        ["XblAuthManager"] = new("Administrador de autenticación de Xbox Live", "Xbox Live Auth Manager",
            "Proporciona autenticación y servicios de autorización para Xbox Live.",
            "Provides authentication and authorization services for interacting with Xbox Live."),
        ["XblGameSave"] = new("Guardado de partidas de Xbox Live", "Xbox Live Game Save",
            "Sincroniza datos de partidas guardadas con Xbox Live.",
            "Syncs save-game data with Xbox Live."),
        ["XboxGipSvc"] = new("Servicio de administración de accesorios de Xbox", "Xbox Accessory Management Service",
            "Administra dispositivos de Xbox conectados.",
            "Manages connected Xbox accessories."),
        ["XboxNetApiSvc"] = new("Servicio de red de Xbox Live", "Xbox Live Networking Service",
            "Admite la red de Windows para Xbox Live.",
            "Supports Windows networking features for Xbox Live."),
        ["bthserv"] = new("Servicio de compatibilidad con Bluetooth", "Bluetooth Support Service",
            "Admite la detección e asociación de dispositivos Bluetooth remotos.",
            "Supports discovery and association of remote Bluetooth devices."),
        ["BTAGService"] = new("Servicio de puerta de enlace de audio Bluetooth", "Bluetooth Audio Gateway Service",
            "Sirve de puerta de enlace de audio para auriculares Bluetooth.",
            "Serves as the audio gateway for Bluetooth audio devices."),
        ["BthAvctpSvc"] = new("Servicio de AVCTP Bluetooth", "AVCTP service",
            "Control de audio/vídeo para dispositivos Bluetooth.",
            "Audio/video control transport for Bluetooth devices."),
        ["hidserv"] = new("Servicio de dispositivos de interfaz humana", "Human Interface Device Service",
            "Activa y mantiene el uso de teclados, mandos y otros HID.",
            "Activates and maintains the use of keyboards, remotes, and other HID devices."),
        ["stisvc"] = new("Adquisición de imágenes de Windows (WIA)", "Windows Image Acquisition (WIA)",
            "Proporciona servicios de captura de imágenes para escáneres y cámaras.",
            "Provides image acquisition services for scanners and cameras."),
        ["ShellHWDetection"] = new("Detección de hardware de Shell", "Shell Hardware Detection",
            "Proporciona notificaciones para eventos de hardware AutoPlay.",
            "Provides notifications for AutoPlay hardware events."),
        ["FontCache"] = new("Servicio de caché de fuentes de Windows", "Windows Font Cache Service",
            "Optimiza el rendimiento de las aplicaciones almacenando en caché datos de fuentes.",
            "Optimizes application performance by caching commonly used font data."),
        ["Schedule"] = new("Programador de tareas", "Task Scheduler",
            "Permite configurar y programar tareas automáticas en este equipo.",
            "Enables you to configure and schedule automated tasks on this computer."),
        ["EventLog"] = new("Registro de eventos de Windows", "Windows Event Log",
            "Administra eventos y registros de eventos.",
            "Manages events and event logs."),
        ["Winmgmt"] = new("Instrumental de administración de Windows", "Windows Management Instrumentation",
            "Proporciona una interfaz y un modelo de objetos comunes para administrar Windows.",
            "Provides a common interface and object model to access management information about the OS."),
        ["Themes"] = new("Temas", "Themes",
            "Proporciona administración de temas de experiencia de usuario.",
            "Provides user experience theme management."),
        ["Audiosrv"] = new("Audio de Windows", "Windows Audio",
            "Administra dispositivos de audio para programas basados en Windows.",
            "Manages audio for Windows-based programs."),
        ["AudioEndpointBuilder"] = new("Generador de puntos de conexión de audio de Windows", "Windows Audio Endpoint Builder",
            "Administra dispositivos de audio para el servicio Audio de Windows.",
            "Manages audio devices for the Windows Audio service."),
        ["Dhcp"] = new("Cliente DHCP", "DHCP Client",
            "Registra y actualiza direcciones IP y registros DNS para este equipo.",
            "Registers and updates IP addresses and DNS records for this computer."),
        ["Dnscache"] = new("Cliente DNS", "DNS Client",
            "Almacena en caché nombres DNS y registra el nombre de este equipo.",
            "Caches Domain Name System (DNS) names and registers this computer’s name."),
        ["LanmanServer"] = new("Servidor", "Server",
            "Admite el uso compartido de archivos, impresoras y canalizaciones con nombre en la red.",
            "Supports file, print, and named-pipe sharing over the network."),
        ["LanmanWorkstation"] = new("Estación de trabajo", "Workstation",
            "Crea y mantiene conexiones de cliente de red a servidores remotos.",
            "Creates and maintains client network connections to remote servers."),
        ["Netman"] = new("Conexiones de red", "Network Connections",
            "Administra objetos en la carpeta Conexiones de red y de acceso telefónico.",
            "Manages objects in the Network and Dial-Up Connections folder."),
        ["NlaSvc"] = new("Conciencia de ubicación de red", "Network Location Awareness",
            "Recopila y almacena información de configuración de red.",
            "Collects and stores configuration information for the network."),
        ["nsi"] = new("Servicio de interfaz de almacén de red", "Network Store Interface Service",
            "Entrega notificaciones de red a clientes en modo de usuario.",
            "Delivers network notifications to user-mode clients."),
        ["WinDefend"] = new("Servicio de Antivirus de Microsoft Defender", "Microsoft Defender Antivirus Service",
            "Ayuda a proteger el equipo frente a malware.",
            "Helps protect your PC against malware."),
        ["WdNisSvc"] = new("Servicio de inspección de red de Antivirus de Microsoft Defender", "Microsoft Defender Antivirus Network Inspection Service",
            "Ayuda a proteger contra la intrusión en protocolos de red conocidos.",
            "Helps guard against intrusion attempts targeting known and newly discovered vulnerabilities."),
        ["mpssvc"] = new("Firewall de Windows Defender", "Windows Defender Firewall",
            "Ayuda a proteger el equipo filtrando el tráfico de red entrante.",
            "Helps protect your computer by filtering inbound network traffic."),
        ["BFE"] = new("Motor de filtrado de base", "Base Filtering Engine",
            "Administra directivas de firewall y de IPsec y filtra el tráfico.",
            "The Base Filtering Engine (BFE) is a service that manages firewall and IPsec policies and implements user-mode filtering."),
        ["CryptSvc"] = new("Servicios de cifrado", "Cryptographic Services",
            "Proporciona tres servicios de administración: base de datos de catálogo, raíz protegida y actualizaciones de certificados.",
            "Provides three management services: Catalog Database Service, Protected Root Service, and Automatic Root Certificate Update Service."),
        ["DcomLaunch"] = new("Iniciador de procesos de servidor DCOM", "DCOM Server Process Launcher",
            "Inicia servidores COM y DCOM en respuesta a peticiones de activación de objetos.",
            "Launches COM and DCOM servers in response to object activation requests."),
        ["RpcSs"] = new("Llamada a procedimiento remoto (RPC)", "Remote Procedure Call (RPC)",
            "Administra el asignador de puntos de conexión RPC y el administrador de objetos COM.",
            "The RPCSS service is the Service Control Manager for COM and DCOM servers."),
        ["RpcEptMapper"] = new("Asignador de puntos de conexión RPC", "RPC Endpoint Mapper",
            "Resuelve identificadores RPC a puntos de conexión de transporte.",
            "Resolves RPC interfaces to transport endpoints."),
        ["PlugPlay"] = new("Plug and Play", "Plug and Play",
            "Permite que un equipo reconozca y se adapte a cambios de hardware.",
            "Enables a computer to recognize and adapt to hardware changes with little or no user input."),
        ["Power"] = new("Energía", "Power",
            "Administra la directiva de energía y la notificación de energía.",
            "Manages power policy and power policy notification delivery."),
        ["ProfSvc"] = new("Servicio de perfil de usuario", "User Profile Service",
            "Carga y descarga perfiles de usuario. Si se detiene, los usuarios no pueden iniciar o cerrar sesión.",
            "This service is responsible for loading and unloading user profiles. If this service is stopped or disabled, users will no longer be able to successfully sign in or sign out."),
        ["SamSs"] = new("Administrador de cuentas de seguridad", "Security Accounts Manager",
            "El inicio de este servicio indica a otros servicios que el SAM está listo.",
            "The startup of this service signals other services that the Security Accounts Manager (SAM) is ready to accept requests."),
        ["SENS"] = new("Servicio de notificación de eventos del sistema", "System Event Notification Service",
            "Supervisa eventos del sistema y notifica a los suscriptores COM+ Event System.",
            "Monitors system events and notifies subscribers to COM+ Event System of these events."),
        ["EventSystem"] = new("Sistema de eventos COM+", "COM+ Event System",
            "Admite el servicio de notificación de eventos del sistema (SENS).",
            "Supports System Event Notification Service (SENS), which provides automatic distribution of events to subscribing COM components."),
        ["BrokerInfrastructure"] = new("Servicio de infraestructura de tareas en segundo plano", "Background Tasks Infrastructure Service",
            "Controla qué aplicaciones en segundo plano se pueden ejecutar en el sistema.",
            "Windows infrastructure service that controls which background tasks can run on the system."),
        ["UserManager"] = new("Administrador de usuarios", "User Manager",
            "Proporciona la funcionalidad de tiempo de ejecución necesaria para la administración de varios usuarios.",
            "User Manager provides the runtime components required for multi-user interaction."),
        ["gpsvc"] = new("Cliente de directiva de grupo", "Group Policy Client",
            "El servicio es responsable de aplicar la configuración configurada por los administradores para el equipo y los usuarios a través del componente de Directiva de grupo.",
            "The service is responsible for applying settings configured by administrators for the computer and users through the Group Policy component."),
        ["ClipSVC"] = new("Servicio de licencia de cliente (ClipSVC)", "Client License Service (ClipSVC)",
            "Proporciona infraestructura para Microsoft Store.",
            "Provides infrastructure support for Microsoft Store."),
        ["LicenseManager"] = new("Servicio de administrador de licencias de Windows", "Windows License Manager Service",
            "Proporciona infraestructura para Microsoft Store.",
            "Provides infrastructure support for Microsoft Store."),
        ["TokenBroker"] = new("Administrador de cuentas web", "Web Account Manager",
            "Este servicio lo usan las aplicaciones para autenticar.",
            "This service is used by Web Account Manager to provide single-sign-on to apps and services."),
        ["wlidsvc"] = new("Asistente de inicio de sesión de cuenta Microsoft", "Microsoft Account Sign-in Assistant",
            "Habilita el inicio de sesión de usuario a través de la identidad de cuenta Microsoft.",
            "Enables user sign-in through Microsoft account identity services."),
        ["CDPSvc"] = new("Servicio de plataforma de dispositivos conectados", "Connected Devices Platform Service",
            "Este servicio se usa para escenarios de dispositivos conectados y de experiencia universal.",
            "This service is used for Connected Devices and Universal Glass scenarios."),
        ["CDPUserSvc"] = new("Servicio de usuario de plataforma de dispositivos conectados", "Connected Devices Platform User Service",
            "Este servicio se usa para escenarios de dispositivos conectados.",
            "This service is used for Connected Devices and Universal Glass scenarios."),
        ["OneSyncSvc"] = new("Host de sincronización", "Sync Host",
            "Este servicio sincroniza correo, contactos, calendario y otros datos de usuario.",
            "This service synchronizes mail, contacts, calendar and various other user data."),
        ["WpnService"] = new("Servicio de sistema de notificaciones de inserción de Windows", "Windows Push Notifications System Service",
            "Este servicio ejecuta la plataforma de notificaciones de inserción de Windows.",
            "This service runs in session 0 and hosts the notification platform and connection provider which handles the connection between the device and WNS server."),
        ["WpnUserService"] = new("Servicio de usuario de notificaciones de inserción de Windows", "Windows Push Notifications User Service",
            "Hospeda la plataforma de notificaciones de inserción de Windows que proporciona compatibilidad con notificaciones locales y de inserción.",
            "This service hosts Windows notification platform which provides support for local and push notifications."),
        ["InstallService"] = new("Servicio de instalación de Microsoft Store", "Microsoft Store Install Service",
            "Proporciona infraestructura para Microsoft Store. Si se detiene, las instalaciones pueden no funcionar.",
            "Provides infrastructure support for the Microsoft Store. This service is started on demand and if disabled, installations may not work correctly."),
        ["AppXSvc"] = new("Servicio de implementación de AppX (AppXSVC)", "AppX Deployment Service (AppXSVC)",
            "Proporciona infraestructura para implementar aplicaciones de la Store.",
            "Provides infrastructure support for deploying Store apps. This service is started on demand."),
        ["StateRepository"] = new("Servicio de repositorio de estado", "State Repository Service",
            "Proporciona la infraestructura necesaria para el modelo de aplicaciones.",
            "Provides required infrastructure for the application model."),
        ["TimeBrokerSvc"] = new("Agente de tiempo", "Time Broker",
            "Coordina la ejecución de trabajo en segundo plano para aplicaciones UWP.",
            "Coordinates execution of background work for WinRT applications."),
        ["SystemEventsBroker"] = new("Agente de eventos del sistema", "System Events Broker",
            "Coordina la ejecución de trabajo en segundo plano para aplicaciones WinRT.",
            "Coordinates execution of background work for WinRT application."),
        ["DusmSvc"] = new("Uso de datos", "Data Usage",
            "Uso de red, límite de datos, restricción en segundo plano y perfiles.",
            "Network data usage, data limit, restrict background data, metered networks."),
        ["PhoneSvc"] = new("Servicio de teléfono", "Phone Service",
            "Administra el estado de telefonía en el dispositivo.",
            "Manages the telephony state on the device."),
        ["icssvc"] = new("Servicio de zona con cobertura inalámbrica móvil de Windows", "Windows Mobile Hotspot Service",
            "Proporciona la capacidad de compartir una conexión de datos con otro dispositivo.",
            "Provides the ability to share a cellular data connection with another device."),
        ["SharedAccess"] = new("Uso compartido de conexión a Internet (ICS)", "Internet Connection Sharing (ICS)",
            "Proporciona servicios de traducción de direcciones de red, direccionamiento, resolución de nombres y prevención de intrusiones.",
            "Provides network address translation, addressing, name resolution and/or intrusion prevention services for a home or small office network."),
        ["TermService"] = new("Servicios de Escritorio remoto", "Remote Desktop Services",
            "Permite a los usuarios conectarse interactivamente a un equipo remoto.",
            "Allows users to connect interactively to a remote computer."),
        ["UmRdpService"] = new("Redireccionador de puertos en modo de usuario de Servicios de Escritorio remoto", "Remote Desktop Services UserMode Port Redirector",
            "Permite el redireccionamiento de impresoras, unidades y puertos para conexiones RDP.",
            "Allows the redirection of printers/drives/ports for RDP connections."),
        ["SessionEnv"] = new("Configuración de Escritorio remoto", "Remote Desktop Configuration",
            "Servicio de configuración y mantenimiento de sesiones de Escritorio remoto.",
            "Remote Desktop Configuration service (RDCS) is responsible for all Remote Desktop Services and Remote Desktop related activities for session creation."),
        ["RasMan"] = new("Administrador de conexiones de acceso remoto", "Remote Access Connection Manager",
            "Administra conexiones de acceso telefónico y de red privada virtual (VPN).",
            "Manages dial-up and virtual private network (VPN) connections from this computer to the Internet or other remote networks."),
        ["WinHttpAutoProxySvc"] = new("Servicio de detección automática de proxy web WinHTTP", "WinHTTP Web Proxy Auto-Discovery Service",
            "Implementa el protocolo WPAD para clientes HTTP.",
            "WinHTTP implements the client HTTP stack and provides developers with a Win32 API and COM Automation component for sending HTTP requests and receiving responses."),
        ["iphlpsvc"] = new("Auxiliar de IP", "IP Helper",
            "Proporciona conectividad de túnel usando tecnologías de transición IPv6.",
            "Provides tunnel connectivity using IPv6 transition technologies (6to4, ISATAP, Port Proxy, and Teredo), and IP-HTTPS."),
        ["SSDPSRV"] = new("Detección SSDP", "SSDP Discovery",
            "Detecta dispositivos y servicios UPnP en la red doméstica.",
            "Discovers networked devices and services that use the SSDP discovery protocol, such as UPnP devices."),
        ["upnphost"] = new("Host de dispositivos UPnP", "UPnP Device Host",
            "Permite el hospedaje de dispositivos UPnP en este equipo.",
            "Allows UPnP devices to be hosted on this computer."),
        ["fdPHost"] = new("Host de proveedor de detección de funciones", "Function Discovery Provider Host",
            "El FDPHOST sirve de proceso host para proveedores de detección de funciones.",
            "The FDPHOST service hosts the Function Discovery (FD) network discovery providers."),
        ["FDResPub"] = new("Publicación de recursos de detección de funciones", "Function Discovery Resource Publication",
            "Publica este equipo y los recursos asociados para que se puedan detectar en la red.",
            "Publishes this computer and resources attached to this computer so they can be discovered over the network."),
        ["lmhosts"] = new("Auxiliar NetBIOS sobre TCP/IP", "TCP/IP NetBIOS Helper",
            "Proporciona compatibilidad para NetBIOS sobre TCP/IP (NetBT) y resolución de nombres NetBIOS.",
            "Provides support for the NetBIOS over TCP/IP (NetBT) service and NetBIOS name resolution for clients on the network."),
        ["seclogon"] = new("Inicio de sesión secundario", "Secondary Logon",
            "Habilita el inicio de procesos bajo credenciales alternativas.",
            "Enables starting processes under alternate credentials."),
        ["PolicyAgent"] = new("Agente de directivas IPsec", "IPsec Policy Agent",
            "Seguridad de Protocolo de Internet (IPsec) admite la autenticación de mismo nivel de red.",
            "Internet Protocol security (IPsec) supports network-level peer authentication, data origin authentication, data integrity, data confidentiality (encryption), and replay protection."),
        ["BDESVC"] = new("Servicio de cifrado de unidad BitLocker", "BitLocker Drive Encryption Service",
            "Proporciona cifrado de unidad BitLocker.",
            "BDESVC hosts the BitLocker Drive Encryption service."),
        ["EFS"] = new("Sistema de cifrado de archivos", "Encrypting File System (EFS)",
            "Proporciona la tecnología de cifrado de archivos principal usada para almacenar archivos cifrados en volúmenes del sistema de archivos NTFS.",
            "Provides the core file encryption technology used to store encrypted files on NTFS file system volumes."),
        ["KeyIso"] = new("Aislamiento de claves CNG", "CNG Key Isolation",
            "El servicio de aislamiento de claves CNG se hospeda en el proceso LSA.",
            "The CNG key isolation service is hosted in the LSA process. The service provides key process isolation to private keys and associated cryptographic operations as required by the Common Criteria."),
        ["VaultSvc"] = new("Administrador de credenciales", "Credential Manager",
            "Proporciona el almacenamiento seguro y la recuperación de credenciales.",
            "Provides secure storage and retrieval of credentials to users, applications and security service packages."),
        ["W32Time"] = new("Hora de Windows", "Windows Time",
            "Mantiene la sincronización de fecha y hora en todos los clientes y servidores de la red.",
            "Maintains date and time synchronization on all clients and servers in the network."),
        ["vds"] = new("Disco virtual", "Virtual Disk",
            "Proporciona servicios de administración para discos, volúmenes, sistemas de archivos y matrices de hardware.",
            "Provides management services for disks, volumes, file systems, and storage arrays."),
        ["swprv"] = new("Proveedor de instantáneas de software de Microsoft", "Microsoft Software Shadow Copy Provider",
            "Administra instantáneas de software tomadas por el servicio Copia de sombra de volumen.",
            "Manages software-based volume shadow copies taken by the Volume Shadow Copy service."),
        ["VSS"] = new("Copia de sombra de volumen", "Volume Shadow Copy",
            "Administra y implementa copias de sombra de volumen usadas para copias de seguridad y otras finalidades.",
            "Manages and implements Volume Shadow Copies used for backup and other purposes."),
        ["defragsvc"] = new("Optimizar unidades", "Optimize drives",
            "Ayuda al equipo a ejecutarse de forma más eficaz consolidando archivos fragmentados en volúmenes de almacenamiento local.",
            "Helps the computer run more efficiently by consolidating fragmented files on storage volumes."),
        ["StorSvc"] = new("Servicio de almacenamiento", "Storage Service",
            "Proporciona servicios de habilitación para la configuración y administración de almacenamiento.",
            "Provides enabling services for storage settings and external storage expansion."),
        ["DisplayEnhancementService"] = new("Servicio de mejora de pantalla", "Display Enhancement Service",
            "Servicio para controlar el brillo de una pantalla en función de la configuración del usuario o de las condiciones ambientales.",
            "A service for managing display enhancement such as brightness control."),
        ["DispBrokerDesktopSvc"] = new("Servicio de directiva de pantalla", "Display Policy Service",
            "Administra la conexión y configuración de pantallas locales y remotas.",
            "Manages the connection and configuration of local and remote displays."),
        ["GraphicsPerfSvc"] = new("Servicio de gráficos de rendimiento", "GraphicsPerfSvc",
            "Servicio de gráficos de rendimiento.",
            "Graphics performance monitor service."),
        ["Appinfo"] = new("Información de la aplicación", "Application Information",
            "Facilita la ejecución de aplicaciones interactivas con privilegios administrativos adicionales.",
            "Facilitates the running of interactive applications with additional administrative privileges."),
        ["AppIDSvc"] = new("Identidad de aplicación", "Application Identity",
            "Determina e informa de la identidad de una aplicación.",
            "Determines and verifies the identity of an application. Disabling this service will prevent AppLocker from being enforced."),
        ["AppReadiness"] = new("App Readiness", "App Readiness",
            "Prepara las aplicaciones para su primer uso cuando un usuario inicia sesión.",
            "Gets apps ready for use the first time a user signs in to this PC and when adding new apps."),
        ["WpcMonSvc"] = new("Controles parentales", "Parental Controls",
            "Este servicio es un límite de espacio aislado para el control parental de Windows.",
            "This service is a stub for Windows Parental Control functionality."),
        ["MapsBroker"] = new("Administrador de mapas descargados", "Downloaded Maps Manager",
            "Servicio de Windows para el acceso a mapas descargados.",
            "Windows service for application access to downloaded maps."),
        ["lfsvc"] = new("Servicio de geolocalización", "Geolocation Service",
            "Este servicio supervisa la ubicación actual del sistema y administra las geocercas.",
            "This service monitors the current location of the system and manages geofences."),
        ["RetailDemo"] = new("Servicio de demostración comercial", "Retail Demo Service",
            "El servicio de demostración comercial controla el modo de demostración del dispositivo.",
            "The Retail Demo service controls device activity while the device is in retail demo mode."),
        ["shpamsvc"] = new("Servicio de administrador de alias de SharedPC", "Shared PC Account Manager",
            "Administra cuentas en un PC compartido.",
            "Manages accounts on a shared PC."),
        ["WalletService"] = new("Servicio de cartera", "WalletService",
            "Hospeda objetos usados por los clientes de la cartera.",
            "Hosts objects used by clients of the wallet."),
        ["DeviceAssociationService"] = new("Servicio de asociación de dispositivos", "Device Association Service",
            "Habilita el emparejamiento entre el sistema y los dispositivos.",
            "Enables pairing between the system and wired or wireless devices."),
        ["DevQueryBroker"] = new("Agente de DevQuery", "DevQuery Background Discovery Broker",
            "Permite que las aplicaciones detecten dispositivos con una tarea en segundo plano.",
            "Enables apps to discover devices with a background task."),
        ["FrameServer"] = new("Servidor de marcos de la cámara de Windows", "Windows Camera Frame Server",
            "Permite que varios clientes tengan acceso a fotogramas de vídeo desde dispositivos de cámara.",
            "Enables multiple clients to access video frames from camera devices."),
        ["FrameServerMonitor"] = new("Monitor del servidor de marcos de la cámara de Windows", "Windows Camera Frame Server Monitor",
            "Supervisa el uso de dispositivos de cámara en Windows.",
            "Monitors the usage of camera devices on Windows."),
        ["CaptureService"] = new("Servicio de captura", "CaptureService",
            "Habilita la captura de pantalla para aplicaciones.",
            "Enables optional screen capture functionality for applications."),
        ["ConsentUxUserSvc"] = new("Administrador de consentimiento de usuario", "ConsentUX User Service",
            "Diálogos de consentimiento para el acceso a recursos.",
            "Consent UX User Service for resource access dialogs."),
        ["PimIndexMaintenanceSvc"] = new("Datos de contactos", "Contact Data",
            "Indiza los datos de contacto para una búsqueda rápida.",
            "Indexes contact data for fast contact searching."),
        ["UnistoreSvc"] = new("Almacenamiento de datos de usuario", "User Data Storage",
            "Administra el almacenamiento de datos de usuario estructurados.",
            "Handles storage of structured user data, including contact info, calendars, messages, and other content."),
        ["UserDataSvc"] = new("Acceso a datos de usuario", "User Data Access",
            "Proporciona a las aplicaciones acceso a datos de usuario estructurados.",
            "Provides apps access to structured user data, including contact info, calendars, messages, and other content."),
        ["MessagingService"] = new("Servicio de mensajería", "MessagingService",
            "Servicio que admite mensajería de texto y funciones relacionadas.",
            "Service supporting text messaging and related functionality."),
        ["PrintWorkflowUserSvc"] = new("Flujo de trabajo de impresión", "Print Workflow",
            "Proporciona compatibilidad con el flujo de trabajo de impresión.",
            "Provides support for Print Workflow applications."),
        ["WlanSvc"] = new("Configuración automática de WLAN", "WLAN AutoConfig",
            "El servicio WLANSVC proporciona la lógica necesaria para configurar, detectar, conectar y desconectarse de una red de área local inalámbrica.",
            "The WLANSVC service provides the logic required to configure, discover, connect to, and disconnect from a wireless local area network (WLAN)."),
        ["dot3svc"] = new("Configuración automática de redes cableadas", "Wired AutoConfig",
            "El servicio Wired AutoConfig (DOT3SVC) se encarga de realizar la autenticación IEEE 802.1X en interfaces Ethernet.",
            "The Wired AutoConfig (DOT3SVC) service is responsible for performing IEEE 802.1X authentication on Ethernet interfaces."),
        ["RmSvc"] = new("Servicio de radio", "Radio Management Service",
            "Servicio de administración de radios y modo avión.",
            "Radio Management and Airplane Mode service."),
        ["NetSetupSvc"] = new("Servicio de instalación de red", "Network Setup Service",
            "Este servicio administra la instalación de controladores de red e inicia el asistente de instalación.",
            "The Network Setup Service manages the installation of network drivers and permits the configuration of low-level network settings."),
        ["NcbService"] = new("Agente de conexión de red", "Network Connection Broker",
            "Permite que las aplicaciones de Microsoft Store reciban notificaciones de la red.",
            "Brokers connections that allow Microsoft Store Apps to receive notifications from the internet."),
        ["PushToInstall"] = new("Servicio Windows PushToInstall", "Windows PushToInstall Service",
            "Proporciona infraestructura para instalaciones remotas de aplicaciones de la Store.",
            "Provides infrastructure support for remotely installing Microsoft Store apps to this PC."),
        ["sppsvc"] = new("Protección de software", "Software Protection",
            "Habilita la descarga, instalación y aplicación de licencias digitales para Windows y aplicaciones.",
            "Enables the download, installation and enforcement of digital licenses for Windows and Windows applications."),
        ["msiserver"] = new("Windows Installer", "Windows Installer",
            "Agrega, modifica y quita aplicaciones proporcionadas como un paquete de Windows Installer (*.msi, *.msp).",
            "Adds, modifies, and removes applications provided as a Windows Installer (*.msi, *.msp) package."),
        ["TrustedInstaller"] = new("Instalador de módulos de Windows", "Windows Modules Installer",
            "Permite la instalación, modificación y eliminación de actualizaciones y componentes opcionales de Windows.",
            "Enables installation, modification, and removal of Windows updates and optional components."),
        ["WinFsp.Launcher"] = new("WinFsp.Launcher", "WinFsp.Launcher",
            "Servicio de lanzamiento de sistemas de archivos de usuario (WinFsp).",
            "WinFsp user-mode file system launcher service."),
        ["DiagSvc"] = new("Host del servicio de diagnóstico", "Diagnostic Execution Service",
            "Ejecuta tareas de diagnóstico para la solución de problemas de Windows.",
            "Executes diagnostic actions for troubleshooting Windows components."),
        ["TroubleshootingSvc"] = new("Servicio recomendado de solución de problemas", "Recommended Troubleshooting Service",
            "Habilita la solución de problemas recomendada para resolver problemas del dispositivo.",
            "Enables automatic recommended troubleshooting to help resolve problems on your device."),
        ["Wecsvc"] = new("Recopilador de eventos de Windows", "Windows Event Collector",
            "Este servicio administra suscripciones persistentes a eventos de orígenes remotos.",
            "This service manages persistent subscriptions to events from remote sources that support WS-Management protocol."),
        ["Netlogon"] = new("Inicio de sesión de red", "Netlogon",
            "Mantiene un canal seguro entre el equipo y el controlador de dominio para autenticar usuarios y servicios.",
            "Maintains a secure channel between this computer and the domain controller for authenticating users and services."),
        ["WebClient"] = new("Cliente web", "WebClient",
            "Permite que programas basados en Windows creen, tengan acceso y modifiquen archivos basados en Internet.",
            "Enables Windows-based programs to create, access, and modify files on Internet-based file systems."),
        ["workfolderssvc"] = new("Carpetas de trabajo", "Work Folders",
            "Este servicio sincroniza archivos con el servidor de Carpetas de trabajo.",
            "This service syncs files with the Work Folders server."),
        ["WcsPlugInService"] = new("Servicio de complementos del sistema de color de Windows", "Windows Color System",
            "El servicio WcsPlugInService hospeda complementos del modelo de dispositivo de color de terceros.",
            "The WcsPlugInService service hosts third-party Windows Color System color device model and gamut map model plug-in modules."),
        ["FontCache3.0.0.0"] = new("Caché de fuentes de Windows Presentation Foundation 3.0.0.0", "Windows Presentation Foundation Font Cache 3.0.0.0",
            "Optimiza el rendimiento de las aplicaciones WPF almacenando en caché datos de fuentes usados con frecuencia.",
            "Optimizes performance of Windows Presentation Foundation (WPF) applications by caching commonly used font data."),
        ["gupdate"] = new("Google Update (gupdate)", "Google Update (gupdate)",
            "Mantiene Google Chrome y otros productos de Google actualizados.",
            "Keeps Google Chrome and other Google software up to date."),
        ["gupdatem"] = new("Google Update (gupdatem)", "Google Update (gupdatem)",
            "Mantiene Google Chrome y otros productos de Google actualizados.",
            "Keeps Google Chrome and other Google software up to date."),
        ["edgeupdate"] = new("Microsoft Edge Update (edgeupdate)", "Microsoft Edge Update (edgeupdate)",
            "Mantiene Microsoft Edge actualizado.",
            "Keeps Microsoft Edge up to date."),
        ["edgeupdatem"] = new("Microsoft Edge Update (edgeupdatem)", "Microsoft Edge Update (edgeupdatem)",
            "Mantiene Microsoft Edge actualizado.",
            "Keeps Microsoft Edge up to date."),
        ["MozillaMaintenance"] = new("Servicio de mantenimiento de Mozilla", "Mozilla Maintenance Service",
            "El Servicio de mantenimiento de Mozilla es necesario para las actualizaciones de Mozilla.",
            "The Mozilla Maintenance Service is required to update Mozilla applications."),
        ["AdobeARMservice"] = new("Adobe Acrobat Update Service", "Adobe Acrobat Update Service",
            "Mantiene Adobe Acrobat y Reader actualizados.",
            "Keeps Adobe Acrobat and Reader up to date."),
        ["NVDisplay.ContainerLocalSystem"] = new("NVIDIA Display Container LS", "NVIDIA Display Container LS",
            "Contenedor de servicios de pantalla NVIDIA.",
            "NVIDIA display services container."),
        ["NvContainerLocalSystem"] = new("NVIDIA LocalSystem Container", "NVIDIA LocalSystem Container",
            "Contenedor de servicios NVIDIA en LocalSystem.",
            "NVIDIA LocalSystem services container."),
        ["Steam Client Service"] = new("Steam Client Service", "Steam Client Service",
            "Steam Client Service ayuda a Steam a realizar funciones del sistema.",
            "Steam Client Service helps Steam perform system-level functions."),
        ["EpicOnlineServices"] = new("Epic Online Services", "Epic Online Services",
            "Servicios en línea de Epic Games.",
            "Epic Games online services."),
        ["Origin Web Helper Service"] = new("Origin Web Helper Service", "Origin Web Helper Service",
            "Ayuda a Origin a realizar funciones del sistema.",
            "Helps Origin perform system-level functions."),
        ["ClickToRunSvc"] = new("Microsoft Office Click-to-Run Service", "Microsoft Office Click-to-Run Service",
            "Administra actualizaciones e instalaciones de Microsoft Office.",
            "Manages Microsoft Office Click-to-Run updates and installations."),
        ["Sense"] = new("Servicio de Microsoft Defender Advanced Threat Protection", "Windows Defender Advanced Threat Protection Service",
            "Servicio de protección avanzada contra amenazas de Microsoft Defender.",
            "Helps protect against advanced attacks and data breaches."),
        ["SecurityHealthService"] = new("Servicio de Windows Security", "Windows Security Service",
            "Escribe información de mantenimiento de WSC que se consume por el Centro de seguridad de Windows.",
            "Windows Security Service handles protection, health, and other security-related features."),
        ["wscsvc"] = new("Centro de seguridad", "Security Center",
            "El servicio WSCSVC (Centro de seguridad) supervisa e informa de la configuración de seguridad.",
            "The WSCSVC (Windows Security Center) service monitors and reports security health settings on the computer."),
        ["NetTcpPortSharing"] = new("Uso compartido de puertos Net.Tcp", "Net.Tcp Port Sharing Service",
            "Proporciona la capacidad de compartir puertos TCP a través de la red.",
            "Provides ability to share TCP ports over the net.tcp protocol."),
        ["W3SVC"] = new("Servicio de publicación World Wide Web", "World Wide Web Publishing Service",
            "Proporciona conectividad y administración web a través de IIS.",
            "Provides Web connectivity and administration through the Internet Information Services manager."),
        ["WAS"] = new("Servicio de activación de procesos de Windows", "Windows Process Activation Service",
            "Proporciona la infraestructura necesaria para la activación de procesos.",
            "Provides process activation, resource management and health management services for message-activated applications."),
        ["IISADMIN"] = new("IIS Admin Service", "IIS Admin Service",
            "Permite la administración de IIS a través de la interfaz de metabase.",
            "Enables this server to administer IIS through the IIS snap-in."),
        ["MSSQL$SQLEXPRESS"] = new("SQL Server (SQLEXPRESS)", "SQL Server (SQLEXPRESS)",
            "Proporciona almacenamiento, procesamiento y acceso controlado a datos.",
            "Provides storage, processing and controlled access of data."),
        ["SQLWriter"] = new("SQL Server VSS Writer", "SQL Server VSS Writer",
            "Proporciona la interfaz para realizar copias de seguridad y restaurar Microsoft SQL Server a través de VSS.",
            "Provides the interface to backup/restore Microsoft SQL Server through the Windows VSS infrastructure."),
        ["PostgreSQL"] = new("PostgreSQL", "PostgreSQL",
            "Servidor de base de datos PostgreSQL.",
            "PostgreSQL database server."),
        ["MongoDB"] = new("MongoDB", "MongoDB",
            "Servidor de base de datos MongoDB.",
            "MongoDB database server."),
        ["docker"] = new("Docker Desktop Service", "Docker Desktop Service",
            "Administra Docker Desktop y los contenedores en este equipo.",
            "Manages Docker Desktop and containers on this computer."),
        ["com.docker.service"] = new("Docker Desktop Service", "Docker Desktop Service",
            "Administra Docker Desktop y los contenedores en este equipo.",
            "Manages Docker Desktop and containers on this computer."),
        ["ssh-agent"] = new("OpenSSH Authentication Agent", "OpenSSH Authentication Agent",
            "Agente que contiene claves privadas usadas para la autenticación de clave pública.",
            "Agent to hold private keys used for public key authentication."),
        ["sshd"] = new("OpenSSH SSH Server", "OpenSSH SSH Server",
            "Servidor SSH para acceso remoto seguro.",
            "SSH server providing secure remote access."),
        ["HvHost"] = new("Servicio de host de HV", "HV Host Service",
            "Proporciona una interfaz para el servicio de aislamiento de Hyper-V.",
            "Provides an interface for the Hyper-V hypervisor to provide per-partition performance counters to the host operating system."),
        ["vmcompute"] = new("Servicio de proceso de host de Hyper-V", "Hyper-V Host Compute Service",
            "Proporciona un servicio de cálculo para máquinas virtuales y contenedores de Hyper-V.",
            "Provides a management service for virtual machines and containers."),
        ["vmms"] = new("Servicio de administración de Hyper-V", "Hyper-V Virtual Machine Management",
            "Servicio de administración para Hyper-V.",
            "Provides management service for Hyper-V."),
    };
}
