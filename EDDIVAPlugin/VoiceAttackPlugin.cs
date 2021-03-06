﻿using EliteDangerousCompanionAppService;
using EliteDangerousDataProviderService;
using EliteDangerousDataDefinitions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using EliteDangerousNetLogMonitor;
using System.Threading;
using System.Diagnostics;
using EliteDangerousStarMapService;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using EliteDangerousSpeechService;

namespace EDDIVAPlugin
{
    public class VoiceAttackPlugin
    {
        private static int minEmpireRatingForTitle = 3;
        private static int minFederationRatingForTitle = 1;

        private static IEDDIStarSystemRepository starSystemRepository;

        // Information obtained from the companion app service
        private static CompanionAppService appService;
        private static Commander Cmdr;

        // Information obtained from the star map service
        private static StarMapService starMapService;

        private static SpeechService speechService;
        
        // Information obtained from the log watcher
        private static Thread logWatcherThread;
        static BlockingCollection<dynamic> LogQueue = new BlockingCollection<dynamic>();
        private static string CurrentEnvironment;

        // Information obtained from EDDI configuration
        private static StarSystem HomeStarSystem;

        private static StarSystem CurrentStarSystem;
        private static StarSystem LastStarSystem;

        private static readonly string ENVIRONMENT_SUPERCRUISE = "Supercruise";
        private static readonly string ENVIRONMENT_NORMAL_SPACE = "Normal space";

        public static readonly string PLUGIN_VERSION = "1.1.0";

        public static string VA_DisplayName()
        {
            return "EDDI " + PLUGIN_VERSION;
        }

        public static string VA_DisplayInfo()
        {
            return "Elite: Dangerous Data Interface\r\nVersion " + PLUGIN_VERSION;
        }

        public static Guid VA_Id()
        {
            return new Guid("{4AD8E3A4-CEFA-4558-B503-1CC9B99A07C1}");
        }

        private static bool initialised = false;
        private static readonly object initLock = new object();
        public static void VA_Init1(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            if (!initialised)
            {
                lock (initLock)
                {
                    if (!initialised)
                    {
                        try
                        {
                            logInfo("EDDI " + PLUGIN_VERSION + " starting");

                            // Set up and/or open our database
                            String dataDir = Environment.GetEnvironmentVariable("AppData") + "\\EDDI";
                            System.IO.Directory.CreateDirectory(dataDir);

                            // Set up our local star system repository
                            starSystemRepository = new EDDIStarSystemSqLiteRepository();

                            // Set up the EDDI configuration
                            EDDIConfiguration eddiConfiguration = EDDIConfiguration.FromFile();
                            setString(ref textValues, "Home system", eddiConfiguration.HomeSystem != null && eddiConfiguration.HomeSystem.Trim().Length > 0 ? eddiConfiguration.HomeSystem : null);
                            setString(ref textValues, "Home system (spoken)", eddiConfiguration.HomeSystem != null && eddiConfiguration.HomeSystem.Trim().Length > 0 ? Translations.StarSystem(eddiConfiguration.HomeSystem) : null);
                            setString(ref textValues, "Home station", eddiConfiguration.HomeStation != null && eddiConfiguration.HomeStation.Trim().Length > 0 ? eddiConfiguration.HomeStation : null);
                            setDecimal(ref decimalValues, "Insurance", eddiConfiguration.Insurance);
                            if (eddiConfiguration.HomeSystem != null && eddiConfiguration.HomeSystem.Trim().Length > 0)
                            {
                                EDDIStarSystem HomeStarSystemData = starSystemRepository.GetEDDIStarSystem(eddiConfiguration.HomeSystem.Trim());
                                if (HomeStarSystemData == null)
                                {
                                    // We have no record of this system; set it up
                                    HomeStarSystemData = new EDDIStarSystem();
                                    HomeStarSystemData.Name = eddiConfiguration.HomeSystem.Trim();
                                    HomeStarSystemData.StarSystem = DataProviderService.GetSystemData(eddiConfiguration.HomeSystem.Trim(), null, null ,null);
                                    HomeStarSystemData.LastVisit = DateTime.Now;
                                    HomeStarSystemData.StarSystemLastUpdated = HomeStarSystemData.LastVisit;
                                    HomeStarSystemData.TotalVisits = 1;
                                    starSystemRepository.SaveEDDIStarSystem(HomeStarSystemData);
                                }
                                HomeStarSystem = HomeStarSystemData.StarSystem;
                            }


                            enableDebugging = eddiConfiguration.Debug;
                            setBoolean(ref booleanValues, "EDDI debug", enableDebugging);
                            logInfo("Debugging is " + enableDebugging);

                            // Set up the app service
                            appService = new CompanionAppService(enableDebugging);
                            if (appService.CurrentState == CompanionAppService.State.READY)
                            {
                                // Carry out initial population of profile
                                InvokeUpdateProfile(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                            }
                            if (Cmdr != null && Cmdr.Name != null)
                            {
                                setString(ref textValues, "EDDI plugin profile status", "Enabled");
                                logInfo("EDDI access to the companion app is enabled");
                            }
                            else
                            {
                                // If InvokeUpdatePlugin failed then it will have have left an error message, but this once we ignore it
                                setPluginStatus(ref textValues, "Operational", null, null);
                                setString(ref textValues, "EDDI plugin profile status", "Disabled");
                                logInfo("EDDI access to the companion app is disabled");
                                // We create a commander anyway, as data such as starsystem uses it
                                Cmdr = new Commander();
                            }

                            // Set up the star map service
                            StarMapConfiguration starMapCredentials = StarMapConfiguration.FromFile();
                            if (starMapCredentials != null && starMapCredentials.apiKey != null)
                            {
                                // Commander name might come from star map credentials or the companion app's profile
                                string commanderName = null;
                                if (starMapCredentials.commanderName != null)
                                {
                                    commanderName = starMapCredentials.commanderName;
                                }
                                else if (Cmdr.Name != null)
                                {
                                    commanderName = Cmdr.Name;
                                }
                                if (commanderName != null)
                                {
                                    starMapService = new StarMapService(starMapCredentials.apiKey, commanderName);
                                    setString(ref textValues, "EDDI plugin EDSM status", "Enabled");
                                    logInfo("EDDI access to EDSM is enabled");
                                }
                            }
                            if (starMapService == null)
                            {
                                setString(ref textValues, "EDDI plugin EDSM status", "Disabled");
                                logInfo("EDDI access to EDSM is disabled");
                            }

                            setString(ref textValues, "EDDI version", PLUGIN_VERSION);

                            speechService = new SpeechService(SpeechServiceConfiguration.FromFile());

                            InvokeNewSystem(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                            CurrentEnvironment = ENVIRONMENT_NORMAL_SPACE;
                            setString(ref textValues, "Environment", CurrentEnvironment);

                            // Set up log monitor
                            NetLogConfiguration netLogConfiguration = NetLogConfiguration.FromFile();
                            if (netLogConfiguration != null && netLogConfiguration.path != null)
                            {
                                logWatcherThread = new Thread(() => StartLogMonitor(netLogConfiguration));
                                logWatcherThread.IsBackground = true;
                                logWatcherThread.Name = "EDDI netlog watcher";
                                logWatcherThread.Start();
                                setString(ref textValues, "EDDI plugin NetLog status", "Enabled");
                                logInfo("EDDI netlog monitor is enabled for " + netLogConfiguration.path);
                            }
                            else
                            {
                                setString(ref textValues, "EDDI plugin NetLog status", "Disabled");
                                logInfo("EDDI netlog monitor is disabled");
                            }

                            setPluginStatus(ref textValues, "Operational", null, null);

                            initialised = true;
                        }
                        catch (Exception ex)
                        {
                            setPluginStatus(ref textValues, "Failed", "Failed to initialise", ex);
                        }
                    }
                }
            }
        }

        public static void StartLogMonitor(NetLogConfiguration configuration)
        {
            if (configuration != null)
            {
                NetLogMonitor monitor = new NetLogMonitor(configuration, (result) => LogQueue.Add(result), enableDebugging);
                monitor.start();
            }
        }

        public static void VA_Exit1(ref Dictionary<string, object> state)
        {
            if (logWatcherThread != null)
            {
                logWatcherThread.Abort();
                logWatcherThread = null;
            }

            if (speechService != null)
            {
                speechService.ShutdownSpeech();
            }

            logInfo("EDDI " + PLUGIN_VERSION + " shutting down");
        }

        public static void VA_Invoke1(String context, ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            switch (context)
            {
                case "coriolis":
                    InvokeCoriolis(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "profile":
                    InvokeUpdateProfile(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "system":
                    InvokeNewSystem(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "log watcher":
                    InvokeLogWatcher(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "say":
                    InvokeSay(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "transmit":
                    InvokeTransmit(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "receive":
                    InvokeReceive(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "generate callsign":
                    InvokeGenerateCallsign(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "system distance":
                    InvokeStarMapSystemDistance(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                case "system note":
                    InvokeStarMapSystemComment(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    return;
                default:
                    if (context.ToLower().StartsWith("event:"))
                    {
                        // Inject an event
                        string data = context.Replace("event: ", "");
                        JObject eventData = JObject.Parse(data);
                        LogQueue.Add(eventData);
                    }
                    return;
            }
        }

        public static void InvokeLogWatcher(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            bool somethingToReport = false;
            while (!somethingToReport)
            {
                try
                {
                    dynamic entry = LogQueue.Take();
                    logDebug("InvokeLogWatcher(): queue has " + LogQueue.Count + " entries");
                    logDebug("InvokeLogWatcher(): entry is " + entry);
                    switch ((string)entry.type)
                    {
                        case "Location": // Change of location
                            if (Cmdr != null)
                            {
                                string newEnvironment;
                                // Tidy up the environment string
                                switch ((string)entry.environment)
                                {
                                    case "Supercruise":
                                        newEnvironment = ENVIRONMENT_SUPERCRUISE;
                                        break;
                                    case "NormalFlight":
                                        newEnvironment = ENVIRONMENT_NORMAL_SPACE;
                                        break;
                                    default:
                                        newEnvironment = (string)entry.environment;
                                        break;
                                }

                                if ((string)entry.starsystem != Cmdr.StarSystem)
                                {
                                    logDebug("InvokeLogWatcher(): system changed from " + Cmdr.StarSystem + " to " + entry.starsystem);
                                    // Change of system
                                    setString(ref textValues, "EDDI event", "System change");
                                    Cmdr.StarSystem = (string)entry.starsystem;
                                    Cmdr.StarSystemX = (decimal?)entry.x;
                                    Cmdr.StarSystemY = (decimal?)entry.y;
                                    Cmdr.StarSystemZ = (decimal?)entry.z;

                                    // Need to fetch new starsystem information
                                    InvokeNewSystem(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                                    logDebug("InvokeLogWatcher(): obtained new system data");
                                    CurrentEnvironment = ENVIRONMENT_SUPERCRUISE;
                                    setString(ref textValues, "Environment", CurrentEnvironment); // Whenever we jump system we always come out in supercruise
                                    somethingToReport = true;
                                }
                                else if (newEnvironment != CurrentEnvironment)
                                {
                                    logDebug("InvokeLogWatcher(): environment changed from " + CurrentEnvironment + " to " + newEnvironment);
                                    // Change of environment
                                    setString(ref textValues, "EDDI event", "Environment change");
                                    CurrentEnvironment = newEnvironment;
                                    setString(ref textValues, "Environment", CurrentEnvironment);
                                    somethingToReport = true;
                                }
                            }
                            break;
                        case "Ship docked": // Ship docked
                            setString(ref textValues, "EDDI event", "Ship docked");
                            // Need to refetch profile information
                            InvokeUpdateProfile(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                            somethingToReport = true;
                            break;
                        case "Ship change": // New or swapped ship
                            logDebug("InvokeLogWatcher(): handling change of ship");
                            setString(ref textValues, "EDDI event", "Ship change");
                            // Need to refetch profile information
                            InvokeUpdateProfile(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                            somethingToReport = true;
                            break;
                        default:
                            setPluginStatus(ref textValues, "Failed", "Unknown log entry " + entry.type, null);
                            break;
                    }
                    if (somethingToReport)
                    {
                        setString(ref textValues, "EDDI raw event", entry.ToString());
                    }
                }
                catch (Exception ex)
                {
                    setPluginStatus(ref textValues, "Failed", "Failed to obtain log entry", ex);
                }
            }
        }

        public static void InvokeCoriolis(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            logDebug("InvokeCoriolis(): entered");
            try
            {
                if (Cmdr == null || Cmdr.Ship == null || Cmdr.Ship.Model == null)
                {
                    // Refetch the profile to set our ship
                    InvokeUpdateProfile(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    if (Cmdr == null || Cmdr.Ship == null || Cmdr.Ship.Model == null)
                    {
                        // Still no luck; assume an error of some sort has been logged by InvokeUpdateProfile()
                        logDebug("InvokeCoriolis(): cannot obtain profile information; leaving");
                        return;
                    }
                }

                string shipUri = Coriolis.ShipUri(Cmdr.Ship);

                logDebug("InvokeCoriolis(): starting process with uri " + shipUri);

                Process.Start(shipUri);

                setPluginStatus(ref textValues, "Operational", null, null);
            }
            catch (Exception e)
            {
                setPluginStatus(ref textValues, "Failed", "Failed to send ship data to coriolis", e);
            }
            logDebug("InvokeCoriolis(): leaving");
        }

        public static void InvokeUpdateProfile(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            logDebug("InvokeUpdateProfile(): entered.  App service is " + (appService == null ? "disabled" : "enabled"));
            if (appService != null)
            {
                try
                {
                    // Obtain the command profile
                    Cmdr = appService.Profile();

                    logDebug("InvokeUpdateProfile(): Commander is " + JsonConvert.SerializeObject(Cmdr));

                    //
                    // Commander data
                    //
                    setString(ref textValues, "Name", Cmdr.Name);
                    setInt(ref intValues, "Combat rating", Cmdr.CombatRating);
                    setString(ref textValues, "Combat rank", Cmdr.CombatRank);
                    setInt(ref intValues, "Trade rating", Cmdr.TradeRating);
                    setString(ref textValues, "Trade rank", Cmdr.TradeRank);
                    setInt(ref intValues, "Explore rating", Cmdr.ExploreRating);
                    setString(ref textValues, "Explore rank", Cmdr.ExploreRank);
                    setInt(ref intValues, "Empire rating", Cmdr.EmpireRating);
                    setString(ref textValues, "Empire rank", Cmdr.EmpireRank);
                    setInt(ref intValues, "Federation rating", Cmdr.FederationRating);
                    setString(ref textValues, "Federation rank", Cmdr.FederationRank);
                    setDecimal(ref decimalValues, "Credits", (decimal)Cmdr.Credits);
                    setString(ref textValues, "Credits (spoken)", humanize(Cmdr.Credits));
                    setDecimal(ref decimalValues, "Debt", (decimal)Cmdr.Debt);
                    setString(ref textValues, "Debt (spoken)", humanize(Cmdr.Debt));


                    //
                    // Ship data
                    //
                    setString(ref textValues, "Ship model", Cmdr.Ship.Model);
                    setString(ref textValues, "Ship model (spoken)", Translations.ShipModel(Cmdr.Ship.Model));
                    setString(ref textValues, "Ship callsign", Cmdr.Ship.CallSign);
                    setString(ref textValues, "Ship callsign (spoken)", Translations.CallSign(Cmdr.Ship.CallSign));
                    setString(ref textValues, "Ship name", Cmdr.Ship.Name);
                    setString(ref textValues, "Ship role", Cmdr.Ship.Role.ToString());
                    setString(ref textValues, "Ship size", Cmdr.Ship.Size.ToString());
                    setDecimal(ref decimalValues, "Ship value", (decimal)Cmdr.Ship.Value);
                    setString(ref textValues, "Ship value (spoken)", humanize(Cmdr.Ship.Value));
                    setDecimal(ref decimalValues, "Ship health", Cmdr.Ship.Health);
                    setInt(ref intValues, "Ship cargo capacity", Cmdr.Ship.CargoCapacity);
                    setInt(ref intValues, "Ship cargo carried", Cmdr.Ship.CargoCarried);
                    // Add number of limpets carried
                    int limpets = 0;
                    foreach (Cargo cargo in Cmdr.Ship.Cargo)
                    {
                        if (cargo.Commodity.Name == "Limpet")
                        {
                            limpets += cargo.Quantity;
                        }
                    }
                    setInt(ref intValues, "Ship limpets carried", limpets);

                    SetModuleDetails("Ship bulkheads", Cmdr.Ship.Bulkheads, ref textValues, ref intValues, ref decimalValues);
                    SetOutfittingCost("Ship bulkheads", Cmdr.Ship.Bulkheads, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    SetModuleDetails("Ship power plant", Cmdr.Ship.PowerPlant, ref textValues, ref intValues, ref decimalValues);
                    SetOutfittingCost("Ship power plant", Cmdr.Ship.PowerPlant, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    SetModuleDetails("Ship thrusters", Cmdr.Ship.Thrusters, ref textValues, ref intValues, ref decimalValues);
                    SetOutfittingCost("Ship thrusters", Cmdr.Ship.Thrusters, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    SetModuleDetails("Ship frame shift drive", Cmdr.Ship.FrameShiftDrive, ref textValues, ref intValues, ref decimalValues);
                    SetOutfittingCost("Ship frame shift drive", Cmdr.Ship.FrameShiftDrive, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    SetModuleDetails("Ship life support", Cmdr.Ship.LifeSupport, ref textValues, ref intValues, ref decimalValues);
                    SetOutfittingCost("Ship life support", Cmdr.Ship.LifeSupport, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    SetModuleDetails("Ship power distributor", Cmdr.Ship.PowerDistributor, ref textValues, ref intValues, ref decimalValues);
                    SetOutfittingCost("Ship power distributor", Cmdr.Ship.PowerDistributor, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    SetModuleDetails("Ship sensors", Cmdr.Ship.Sensors, ref textValues, ref intValues, ref decimalValues);
                    SetOutfittingCost("Ship sensors", Cmdr.Ship.Sensors, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    SetModuleDetails("Ship fuel tank", Cmdr.Ship.FuelTank, ref textValues, ref intValues, ref decimalValues);
                    SetOutfittingCost("Ship fuel tank", Cmdr.Ship.FuelTank, ref Cmdr.Outfitting, ref textValues, ref decimalValues);

                    // Hardpoints
                    int numTinyHardpoints = 0;
                    int numSmallHardpoints = 0;
                    int numMediumHardpoints = 0;
                    int numLargeHardpoints = 0;
                    int numHugeHardpoints = 0;
                    foreach (Hardpoint Hardpoint in Cmdr.Ship.Hardpoints)
                    {
                        string baseHardpointName = "";
                        switch (Hardpoint.Size)
                        {
                            case 0:
                                baseHardpointName = "Ship tiny hardpoint " + ++numTinyHardpoints;
                                break;
                            case 1:
                                baseHardpointName = "Ship small hardpoint " + ++numSmallHardpoints;
                                break;
                            case 2:
                                baseHardpointName = "Ship medium hardpoint " + ++numMediumHardpoints;
                                break;
                            case 3:
                                baseHardpointName = "Ship large hardpoint " + ++numLargeHardpoints;
                                break;
                            case 4:
                                baseHardpointName = "Ship huge hardpoint " + ++numHugeHardpoints;
                                break;
                        }

                        setBoolean(ref booleanValues, baseHardpointName + " occupied", Hardpoint.Module != null);
                        SetModuleDetails(baseHardpointName + " module", Hardpoint.Module, ref textValues, ref intValues, ref decimalValues);
                        SetOutfittingCost(baseHardpointName + " module", Hardpoint.Module, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    }

                    setInt(ref intValues, "Ship hardpoints", numSmallHardpoints + numMediumHardpoints + numLargeHardpoints + numHugeHardpoints);
                    setInt(ref intValues, "Ship utility slots", numTinyHardpoints);

                    // Compartments
                    int curCompartment = 0;
                    foreach (Compartment Compartment in Cmdr.Ship.Compartments)
                    {
                        string baseCompartmentName = "Ship compartment " + ++curCompartment;
                        setInt(ref intValues, baseCompartmentName + " size", Compartment.Size);
                        setBoolean(ref booleanValues, baseCompartmentName + " occupied", Compartment.Module != null);
                        SetModuleDetails(baseCompartmentName + " module", Compartment.Module, ref textValues, ref intValues, ref decimalValues);
                        SetOutfittingCost(baseCompartmentName + " module", Compartment.Module, ref Cmdr.Outfitting, ref textValues, ref decimalValues);
                    }
                    setInt(ref intValues, "Ship compartments", curCompartment);

                    //
                    // Stored ships data
                    //
                    int currentStoredShip = 1;
                    foreach (Ship StoredShip in Cmdr.StoredShips)
                    {
                        string varBase = "Stored ship " + currentStoredShip;
                        setString(ref textValues, varBase + " model", StoredShip.Model);
                        setString(ref textValues, varBase + " system", StoredShip.StarSystem);
                        setString(ref textValues, varBase + " station", StoredShip.Station);
                        setString(ref textValues, varBase + " callsign", StoredShip.CallSign);
                        setString(ref textValues, varBase + " callsign (spoken)", Translations.CallSign(StoredShip.CallSign));
                        setString(ref textValues, varBase + " name", StoredShip.Name);
                        setString(ref textValues, varBase + " role", StoredShip.Role.ToString());

                        // Fetch the star system in which the ship is stored
                        EDDIStarSystem StoredShipStarSystemData = starSystemRepository.GetEDDIStarSystem(StoredShip.StarSystem);
                        if (StoredShipStarSystemData == null)
                        {
                            // We have no record of this system; set it up
                            StoredShipStarSystemData = new EDDIStarSystem();
                            StoredShipStarSystemData.Name = StoredShip.StarSystem;
                            StoredShipStarSystemData.StarSystem = DataProviderService.GetSystemData(StoredShip.StarSystem, null, null, null);
                            StoredShipStarSystemData.LastVisit = DateTime.Now;
                            StoredShipStarSystemData.StarSystemLastUpdated = StoredShipStarSystemData.LastVisit;
                            StoredShipStarSystemData.TotalVisits = 1;
                            starSystemRepository.SaveEDDIStarSystem(StoredShipStarSystemData);
                        }

                        // Have to grab a local copy of our star system as CurrentStarSystem might not have been initialised yet
                        EDDIStarSystem ThisStarSystemData = starSystemRepository.GetEDDIStarSystem(Cmdr.StarSystem);

                        // Work out the distance to the system where the ship is stored if we can
                        if (ThisStarSystemData != null && ThisStarSystemData.StarSystem != null && ThisStarSystemData.StarSystem.X != null && StoredShipStarSystemData.StarSystem != null && StoredShipStarSystemData.StarSystem.X != null)
                        {
                            decimal distance = (decimal)Math.Round(Math.Sqrt(Math.Pow((double)(ThisStarSystemData.StarSystem.X - StoredShipStarSystemData.StarSystem.X), 2)
                                + Math.Pow((double)(ThisStarSystemData.StarSystem.Y - StoredShipStarSystemData.StarSystem.Y), 2)
                                + Math.Pow((double)(ThisStarSystemData.StarSystem.Z - StoredShipStarSystemData.StarSystem.Z), 2)), 2);
                            setDecimal(ref decimalValues, varBase + " distance", distance);
                        }
                        else
                        {
                            // We don't know how far away the ship is
                            setDecimal(ref decimalValues, varBase + " distance", (decimal?)null);
                        }

                        currentStoredShip++;
                    }
                    setInt(ref intValues, "Stored ships", Cmdr.StoredShips.Count);
                    // We also clear out any ships that have been sold since the last run.  We don't know
                    // how many there are so just clear out the succeeding 10 slots and hope that the commander
                    // hasn't gone on a selling spree
                    for (int i = 0; i < 10; i++)
                    {
                        string varBase = "Stored ship " + (currentStoredShip + i);
                        setString(ref textValues, varBase + " model", null);
                        setString(ref textValues, varBase + " system", null);
                        setString(ref textValues, varBase + " station", null);
                        setString(ref textValues, varBase + " callsign", null);
                        setString(ref textValues, varBase + " callsign (spoken)", null);
                        setString(ref textValues, varBase + " name", null);
                        setString(ref textValues, varBase + " role", null);
                        setDecimal(ref decimalValues, varBase + " distance", null);
                    }

                    setString(ref textValues, "Last station name", Cmdr.LastStation);

                    if (enableDebugging)
                    {
                        logDebug("InvokeUpdateProfile(): Resultant shortint values " + JsonConvert.SerializeObject(shortIntValues));
                        logDebug("InvokeUpdateProfile(): Resultant text values " + JsonConvert.SerializeObject(textValues));
                        logDebug("InvokeUpdateProfile(): Resultant int values " + JsonConvert.SerializeObject(intValues));
                        logDebug("InvokeUpdateProfile(): Resultant decimal values " + JsonConvert.SerializeObject(decimalValues));
                        logDebug("InvokeUpdateProfile(): Resultant boolean values " + JsonConvert.SerializeObject(booleanValues));
                        logDebug("InvokeUpdateProfile(): Resultant datetime values " + JsonConvert.SerializeObject(dateTimeValues));
                    }

                    setPluginStatus(ref textValues, "Operational", null, null);
                    setString(ref textValues, "EDDI plugin profile status", "Enabled");
                }
                catch (Exception ex)
                {
                    setPluginStatus(ref textValues, "Failed", "Failed to access profile", ex);
                    setString(ref textValues, "EDDI plugin profile status", "Disabled");
                }
            }
        }


        /// <summary>Find a module in outfitting that matches our existing module and provide its price</summary>
        private static void SetModuleDetails(string name, Module module, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues)
        {
            setString(ref textValues, name, module == null ? null : module.Name);
            setInt(ref intValues, name + " class", module == null ? (int?)null : module.Class);
            setString(ref textValues, name + " grade", module == null ? null : module.Grade);
            setDecimal(ref decimalValues, name + " health", module == null ? (decimal?)null : module.Health);
            setDecimal(ref decimalValues, name + " cost", module == null ? (decimal?)null : (decimal)module.Cost);
            setDecimal(ref decimalValues, name + " value", module == null ? (decimal?)null : (decimal)module.Value);
            if (module != null && module.Cost < module.Value)
            {
                decimal discount = Math.Round((1 - (((decimal)module.Cost) / ((decimal)module.Value))) * 100, 1);
                setDecimal(ref decimalValues, name + " discount", discount > 0.01M ? discount : (decimal?)null);
            }
            else
            {
                setDecimal(ref decimalValues, name + " discount", null);
            }
        }

        /// <summary>Find a module in outfitting that matches our existing module and provide its price</summary>
        private static void SetOutfittingCost(string name, Module existing, ref List<Module> outfittingModules, ref Dictionary<string, string> textValues, ref Dictionary<string, decimal?> decimalValues)
        {
            if (existing != null)
            {
                foreach (Module Module in outfittingModules)
                {
                    if (existing.EDDBID == Module.EDDBID)
                    {
                        // Found it
                        setDecimal(ref decimalValues, name + " station cost", (decimal?)Module.Cost);
                        if (Module.Cost < existing.Cost)
                        {
                            // And it's cheaper
                            setDecimal(ref decimalValues, name + " station discount", existing.Cost - Module.Cost);
                            setString(ref textValues, name + " station discount (spoken)", humanize(existing.Cost - Module.Cost));
                        }
                        return;
                    }
                }
            }
            // Not found so remove any existing
            setDecimal(ref decimalValues, "Ship " + name + " station cost", (decimal?)null);
            setDecimal(ref decimalValues, "Ship " + name + " station discount", (decimal?)null);
            setString(ref textValues, "Ship " + name + " station discount (spoken)", (string)null);
        }

        public static void InvokeNewSystem(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            logDebug("InvokeNewSystem() entered");
            try
            {
                if (Cmdr == null)
                {
                    logDebug("InvokeNewSystem() Cmdr is NULL - attempting to refetch");
                    // Refetch the profile to set our system
                    InvokeUpdateProfile(ref state, ref shortIntValues, ref textValues, ref intValues, ref decimalValues, ref booleanValues, ref dateTimeValues, ref extendedValues);
                    if (Cmdr == null)
                    {
                        // Still no luck; assume an error of some sort has been logged by InvokeUpdateProfile()
                        logDebug("InvokeNewSystem() Cmdr remained NULL - giving up");
                        return;
                    }
                }

                logDebug("InvokeNewSystem() CurrentStarSystem is " +  (CurrentStarSystem == null ? "<null>" : JsonConvert.SerializeObject(CurrentStarSystem)));
                logDebug("InvokeNewSystem() Cmdr is " + (Cmdr == null ? "<null>" : JsonConvert.SerializeObject(Cmdr)));

                if (Cmdr.StarSystem == null)
                {
                    // No information available
                    logDebug("InvokeNewSystem() No starsystem data available");
                    return;
                }

                bool RecordUpdated = false;
                if ((!initialised) || CurrentStarSystem == null || Cmdr.StarSystem != CurrentStarSystem.Name)
                {
                    logDebug("InvokeNewSystem() In init or starsystem has changed");
                    // The star system has changed or we're in init; obtain the data ready for setting the VA values
                    EDDIStarSystem CurrentStarSystemData = starSystemRepository.GetEDDIStarSystem(Cmdr.StarSystem);
                    logDebug("InvokeNewSystem() CurrentStarSystemData is " + (CurrentStarSystemData == null ? "<null>" : JsonConvert.SerializeObject(CurrentStarSystemData)));
                    if (CurrentStarSystemData == null)
                    {
                        logDebug("InvokeNewSystem() Creating new starsystemdata");
                        // We have no record of this system; set it up
                        CurrentStarSystemData = new EDDIStarSystem();
                        CurrentStarSystemData.Name = Cmdr.StarSystem;
                        CurrentStarSystemData.StarSystem = DataProviderService.GetSystemData(Cmdr.StarSystem, Cmdr.StarSystemX, Cmdr.StarSystemY, Cmdr.StarSystemZ);
                        CurrentStarSystemData.LastVisit = DateTime.Now;
                        CurrentStarSystemData.StarSystemLastUpdated = CurrentStarSystemData.LastVisit;
                        CurrentStarSystemData.TotalVisits = 1;
                        RecordUpdated = true;
                    }
                    else
                    {
                        logDebug("InvokeNewSystem() Checking existing starsystemdata");
                        if (CurrentStarSystemData.StarSystem == null || (DateTime.Now - CurrentStarSystemData.StarSystemLastUpdated).TotalHours > 12)
                        {
                            logDebug("InvokeNewSystem() Refreshing stale or missing data");
                            // Data is stale; refresh it
                            CurrentStarSystemData.StarSystem = DataProviderService.GetSystemData(CurrentStarSystemData.Name, Cmdr.StarSystemX, Cmdr.StarSystemY, Cmdr.StarSystemZ);
                            CurrentStarSystemData.StarSystemLastUpdated = CurrentStarSystemData.LastVisit;
                            RecordUpdated = true;
                        }
                        // Only update if we have moved (as opposed to reinitialising here)
                        if (initialised)
                        {
                            logDebug("InvokeNewSystem() Updating visit information");
                            CurrentStarSystemData.PreviousVisit = CurrentStarSystemData.LastVisit;
                            CurrentStarSystemData.LastVisit = DateTime.Now;
                            CurrentStarSystemData.TotalVisits++;
                            RecordUpdated = true;
                        }
                    }
                    logDebug("InvokeNewSystem() CurrentStarSystemData is now " + (CurrentStarSystemData == null ? "<null>" : JsonConvert.SerializeObject(CurrentStarSystemData)));
                    if (RecordUpdated)
                    {
                        logDebug("InvokeNewSystem() Storing updated starsystemdata");
                        starSystemRepository.SaveEDDIStarSystem(CurrentStarSystemData);
                    }

                    StarSystem ThisStarSystem = CurrentStarSystemData.StarSystem;
                    LastStarSystem = CurrentStarSystem;
                    CurrentStarSystem = ThisStarSystem;

                    logDebug("InvokeNewSystem() CurrentStarSystem is now " + (CurrentStarSystem == null ? "<null>" : JsonConvert.SerializeObject(CurrentStarSystem)));
                    logDebug("InvokeNewSystem() LastStarSystem is now " + (LastStarSystem == null ? "<null>" : JsonConvert.SerializeObject(LastStarSystem)));

                    if (initialised && LastStarSystem != null && LastStarSystem.Name != CurrentStarSystem.Name)
                    {
                        // We have travelled; let EDSM know
                        if (starMapService != null)
                        {
                            logDebug("InvokeNewSystem() Sending update to EDSM");
                            // Take information directly from the commander structure as it hasn't been munged
                            starMapService.sendStarMapLog(Cmdr.StarSystem, Cmdr.StarSystemX, Cmdr.StarSystemY, Cmdr.StarSystemZ);
                            logDebug("InvokeNewSystem() Update sent");
                        }
                    }

                    logDebug("InvokeNewSystem() Setting system information");
                    setString(ref textValues, "System name", CurrentStarSystem.Name);
                    setString(ref textValues, "System name (spoken)", Translations.StarSystem(CurrentStarSystem.Name));
                    setInt(ref intValues, "System visits", CurrentStarSystemData.TotalVisits);
                    setDateTime(ref dateTimeValues, "System previous visit", CurrentStarSystemData.PreviousVisit);
                    setInt(ref intValues, "System minutes since previous visit", CurrentStarSystemData.PreviousVisit == null ? (int?)null : (int)(DateTime.Now - (DateTime)CurrentStarSystemData.PreviousVisit).TotalMinutes);
                    setDecimal(ref decimalValues, "System population", (decimal?)CurrentStarSystem.Population);
                    setString(ref textValues, "System population (spoken)", humanize(CurrentStarSystem.Population));
                    setString(ref textValues, "System allegiance", CurrentStarSystem.Allegiance);
                    setString(ref textValues, "System government", CurrentStarSystem.Government);
                    setString(ref textValues, "System faction", CurrentStarSystem.Faction);
                    setString(ref textValues, "System primary economy", CurrentStarSystem.PrimaryEconomy);
                    setString(ref textValues, "System state", CurrentStarSystem.State);
                    setString(ref textValues, "System security", CurrentStarSystem.Security);
                    setString(ref textValues, "System power", CurrentStarSystem.Power);
                    setString(ref textValues, "System power (spoken)", Translations.Power(CurrentStarSystem.Power));
                    setString(ref textValues, "System power state", CurrentStarSystem.PowerState);
                    setDecimal(ref decimalValues, "System X", CurrentStarSystem.X);
                    setDecimal(ref decimalValues, "System Y", CurrentStarSystem.Y);
                    setDecimal(ref decimalValues, "System Z", CurrentStarSystem.Z);
                    logDebug("InvokeNewSystem() Set system information");

                    logDebug("InvokeNewSystem() Setting system rank");
                    // Allegiance-specific rank
                    string systemRank = "Commander";
                    if (Cmdr.Name != null) // using Name as a canary to see if the data is missing
                    {
                        if (CurrentStarSystem.Allegiance == "Federation" && Cmdr.FederationRating >= minFederationRatingForTitle)
                        {
                            systemRank = Cmdr.FederationRank;
                        }
                        else if (CurrentStarSystem.Allegiance == "Empire" && Cmdr.EmpireRating >= minEmpireRatingForTitle)
                        {
                            systemRank = Cmdr.EmpireRank;
                        }
                    }
                    setString(ref textValues, "System rank", systemRank);
                    logDebug("InvokeNewSystem() Set system rank");

                    // Stations
                    logDebug("InvokeNewSystem() Setting station information");
                    foreach (Station Station in CurrentStarSystem.Stations)
                    {
                        setString(ref textValues, "System station name", Station.Name);
                    }
                    setInt(ref intValues, "System stations", CurrentStarSystem.Stations.Count);
                    setInt(ref intValues, "System starports", CurrentStarSystem.Stations.Count(s => s.IsStarport()));
                    setInt(ref intValues, "System outposts", CurrentStarSystem.Stations.Count(s => s.IsOutpost()));
                    setInt(ref intValues, "System planetary stations", CurrentStarSystem.Stations.Count(s => s.IsPlanetary()));
                    setInt(ref intValues, "System planetary outposts", CurrentStarSystem.Stations.Count(s => s.IsPlanetaryOutpost()));
                    setInt(ref intValues, "System planetary ports", CurrentStarSystem.Stations.Count(s => s.IsPlanetaryPort()));
                    logDebug("InvokeNewSystem() Set station information");

                    logDebug("InvokeNewSystem() Setting distance from home");
                    if (HomeStarSystem != null && HomeStarSystem.X != null && CurrentStarSystem.X != null)
                    {
                        setDecimal(ref decimalValues, "System distance from home", (decimal)Math.Round(Math.Sqrt(Math.Pow((double)(CurrentStarSystem.X - HomeStarSystem.X), 2) + Math.Pow((double)(CurrentStarSystem.Y - HomeStarSystem.Y), 2) + Math.Pow((double)(CurrentStarSystem.Z - HomeStarSystem.Z), 2)), 2));
                    }
                    logDebug("InvokeNewSystem() Set distance from home");

                    logDebug("InvokeNewSystem() Setting EDSM comment");
                    if (starMapService != null)
                    {
                        StarMapInfo info = starMapService.getStarMapInfo(CurrentStarSystem.Name);
                        setString(ref textValues, "System comment", info == null || info.Comment == null || info.Comment.Trim() == "" ? null : info.Comment);
                    }
                    logDebug("InvokeNewSystem() Set EDSM comment");

                    if (LastStarSystem != null)
                    {
                        logDebug("InvokeNewSystem() Setting last system information");
                        setString(ref textValues, "Last system name", LastStarSystem.Name);
                        setString(ref textValues, "Last system name (spoken)", Translations.StarSystem(LastStarSystem.Name));
                        setDecimal(ref decimalValues, "Last system population", (decimal?)LastStarSystem.Population);
                        setString(ref textValues, "Last system population (spoken)", humanize(LastStarSystem.Population));
                        setString(ref textValues, "Last system allegiance", LastStarSystem.Allegiance);
                        setString(ref textValues, "Last system government", LastStarSystem.Government);
                        setString(ref textValues, "Last system faction", LastStarSystem.Faction);
                        setString(ref textValues, "Last system primary economy", LastStarSystem.PrimaryEconomy);
                        setString(ref textValues, "Last system state", LastStarSystem.State);
                        setString(ref textValues, "Last system security", LastStarSystem.Security);
                        setString(ref textValues, "Last system power", LastStarSystem.Power);
                        setString(ref textValues, "Last system power (spoken)", Translations.Power(LastStarSystem.Power));
                        setString(ref textValues, "Last system power state", LastStarSystem.PowerState);
                        setDecimal(ref decimalValues, "Last system X", LastStarSystem.X);
                        setDecimal(ref decimalValues, "Last system Y", LastStarSystem.Y);
                        setDecimal(ref decimalValues, "Last system Z", LastStarSystem.Z);
                        logDebug("InvokeNewSystem() Set last system information");

                        logDebug("InvokeNewSystem() Setting last jump");
                        if (LastStarSystem.X != null && CurrentStarSystem.X != null)
                        {
                            setDecimal(ref decimalValues, "Last jump", (decimal)Math.Round(Math.Sqrt(Math.Pow((double)(CurrentStarSystem.X - LastStarSystem.X), 2) + Math.Pow((double)(CurrentStarSystem.Y - LastStarSystem.Y), 2) + Math.Pow((double)(CurrentStarSystem.Z - LastStarSystem.Z), 2)), 2));
                        }
                        logDebug("InvokeNewSystem() Set last jump");

                        logDebug("InvokeNewSystem() Setting last system rank");
                        // Allegiance-specific rank
                        string lastSystemRank = "Commander";
                        if (Cmdr.Name != null) // using Name as a canary to see if the data is missing
                        {
                            if (LastStarSystem.Allegiance == "Federation" && Cmdr.FederationRating >= minFederationRatingForTitle)
                            {
                                lastSystemRank = Cmdr.FederationRank;
                            }
                            else if (LastStarSystem.Allegiance == "Empire" && Cmdr.EmpireRating >= minEmpireRatingForTitle)
                            {
                                lastSystemRank = Cmdr.EmpireRank;
                            }
                        }
                        setString(ref textValues, "Last system rank", systemRank);
                        logDebug("InvokeNewSystem() Set last system rank");

                        // Stations
                        logDebug("InvokeNewSystem() Setting last system station information");
                        foreach (Station Station in LastStarSystem.Stations)
                        {
                            setString(ref textValues, "Last system station name", Station.Name);
                        }
                        setInt(ref intValues, "Last system stations", LastStarSystem.Stations.Count);
                        setInt(ref intValues, "Last system starports", LastStarSystem.Stations.Count(s => s.IsStarport()));
                        setInt(ref intValues, "Last system outposts", LastStarSystem.Stations.Count(s => s.IsOutpost()));
                        setInt(ref intValues, "Last system planetary stations", LastStarSystem.Stations.Count(s => s.IsPlanetary()));
                        setInt(ref intValues, "Last system planetary outposts", LastStarSystem.Stations.Count(s => s.IsPlanetaryOutpost()));
                        setInt(ref intValues, "Last system planetary ports", LastStarSystem.Stations.Count(s => s.IsPlanetaryPort()));
                        logDebug("InvokeNewSystem() Set last system station information");
                    }
                }

                if (enableDebugging)
                {
                    logDebug("InvokeNewSystem(): Resultant shortint values " + JsonConvert.SerializeObject(shortIntValues));
                    logDebug("InvokeNewSystem(): Resultant text values " + JsonConvert.SerializeObject(textValues));
                    logDebug("InvokeNewSystem(): Resultant int values " + JsonConvert.SerializeObject(intValues));
                    logDebug("InvokeNewSystem(): Resultant decimal values " + JsonConvert.SerializeObject(decimalValues));
                    logDebug("InvokeNewSystem(): Resultant boolean values " + JsonConvert.SerializeObject(booleanValues));
                    logDebug("InvokeNewSystem(): Resultant datetime values " + JsonConvert.SerializeObject(dateTimeValues));
                }

                setPluginStatus(ref textValues, "Operational", null, null);
            }
            catch (Exception e)
            {
                setPluginStatus(ref textValues, "Failed", "Failed to obtain system data", e);
            }
        }

        /// <summary>
        /// Say something inside the cockpit with text-to-speech
        /// </summary>
        public static void InvokeSay(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            try
            {
                string script = null;
                foreach (string key in textValues.Keys)
                {
                    if (key.EndsWith(" script"))
                    {
                        script = textValues[key];
                        break;
                    }
                }
                if (script == null)
                {
                    return;
                }
                speechService.Say(Cmdr, Cmdr.Ship, script);
            }
            catch (Exception e)
            {
                setPluginStatus(ref textValues, "Failed", "Failed to run internal speech system", e);
            }
        }

        /// <summary>
        /// Transmit something on the radio with text-to-speech
        /// </summary>
        public static void InvokeTransmit(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            try
            {
                string script = null;
                foreach (string key in textValues.Keys)
                {
                    if (key.EndsWith(" script"))
                    {
                        script = textValues[key];
                        break;
                    }
                }
                if (script == null)
                {
                    return;
                }
                speechService.Transmit(Cmdr, Cmdr.Ship, script);
            }
            catch (Exception e)
            {
                setPluginStatus(ref textValues, "Failed", "Failed to run internal speech system", e);
            }
        }

        /// <summary>
        /// Receive something on the radio with text-to-speech
        /// </summary>
        public static void InvokeReceive(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            try
            {
                string script = null;
                foreach (string key in textValues.Keys)
                {
                    if (key.EndsWith(" script"))
                    {
                        script = textValues[key];
                        break;
                    }
                }
                if (script == null)
                {
                    return;
                }
                speechService.Receive(Cmdr, Cmdr.Ship, script);
            }
            catch (Exception e)
            {
                setPluginStatus(ref textValues, "Failed", "Failed to run internal speech system", e);
            }
        }

        /// <summary>
        /// Generate a callsign
        /// </summary>
        public static void InvokeGenerateCallsign(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            try
            {
                string callsign = Ship.generateCallsign();
                setString(ref textValues, "EDDI generated callsign", callsign);
                setString(ref textValues, "EDDI generated callsign (spoken)", Translations.CallSign(callsign));
            }
            catch (Exception e)
            {
                setPluginStatus(ref textValues, "Failed", "Failed to generate callsign", e);
            }
        }

        /// <summary>
        /// Send a system distance to the starmap service
        /// </summary>
        public static void InvokeStarMapSystemDistance(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            try
            {
                string system = null;
                foreach (string key in textValues.Keys)
                {
                    if (key == "EDDI remote system name")
                    {
                        system = textValues[key];
                    }
                }
                if (system == null)
                {
                    return;
                }

                decimal? distance = null;
                foreach (string key in decimalValues.Keys)
                {
                    if (key == "EDDI remote system distance")
                    {
                        distance = decimalValues[key];
                    }
                }
                if (distance == null)
                {
                    return;
                }

                if (Cmdr != null && starMapService != null)
                {
                    starMapService.sendStarMapDistance(Cmdr.StarSystem, system, (decimal)distance);
                }
            }
            catch (Exception e)
            {
                setPluginStatus(ref textValues, "Failed", "Failed to send system distance to EDSM", e);
            }
        }

        /// <summary>
        /// Send a comment to the starmap service
        /// </summary>
        public static void InvokeStarMapSystemComment(ref Dictionary<string, object> state, ref Dictionary<string, Int16?> shortIntValues, ref Dictionary<string, string> textValues, ref Dictionary<string, int?> intValues, ref Dictionary<string, decimal?> decimalValues, ref Dictionary<string, Boolean?> booleanValues, ref Dictionary<string, DateTime?> dateTimeValues, ref Dictionary<string, object> extendedValues)
        {
            try
            {
                string comment = null;
                foreach (string key in textValues.Keys)
                {
                    if (key == "EDDI system comment")
                    {
                        comment = textValues[key];
                    }
                }
                if (comment == null)
                {
                    return;
                }

                if (Cmdr != null && starMapService != null)
                {
                    starMapService.sendStarMapComment(Cmdr.StarSystem, comment);
                }
            }
            catch (Exception e)
            {
                setPluginStatus(ref textValues, "Failed", "Failed to send system comment to EDSM", e);
            }
        }

        private static void setInt(ref Dictionary<string, int?> values, string key, int? value)
        {
            if (values.ContainsKey(key))
                values[key] = value;
            else
                values.Add(key, value);
        }

        private static void setDecimal(ref Dictionary<string, decimal?> values, string key, decimal? value)
        {
            if (values.ContainsKey(key))
                values[key] = value;
            else
                values.Add(key, value);
        }

        private static void setBoolean(ref Dictionary<string, bool?> values, string key, bool? value)
        {
            if (values.ContainsKey(key))
                values[key] = value;
            else
                values.Add(key, value);
        }

        private static void setString(ref Dictionary<string, string> values, string key, string value)
        {
            if (values.ContainsKey(key))
                values[key] = value;
            else
                values.Add(key, value);
        }

        private static void setDateTime(ref Dictionary<string, DateTime?> values, string key, DateTime? value)
        {
            if (values.ContainsKey(key))
                values[key] = value;
            else
                values.Add(key, value);
        }

        private static void setPluginStatus(ref Dictionary<string, string> values, string status, string error, Exception exception)
        {
            setString(ref values, "EDDI status", status);
            if (status == "Operational")
            {
                setString(ref values, "EDDI error", null);
                setString(ref values, "EDDI exception", null);
            }
            else
            {
                logError("EDDI error: " + error);
                logError("EDDI exception: " + (exception == null ? "<null>" : exception.ToString()));
                setString(ref values, "EDDI error", error);
                setString(ref values, "EDDI exception", exception == null ? null : exception.ToString());
            }
        }

        public static string humanize(long? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value == 0)
            {
                return "zero";
            }

            int number;
            int nextDigit;
            string order;
            int digits = (int)Math.Log10((double)value);
            if (digits < 3)
            {
                // Units
                number = (int)value;
                order = "";
                nextDigit = 0;
            }
            else if (digits < 6)
            {
                // Thousands
                number = (int)(value / 1000);
                order = "thousand";
                nextDigit = (((int)value) - (number * 1000)) / 100;
            }
            else if (digits < 9)
            {
                // Millions
                number = (int)(value / 1000000);
                order = "million";
                nextDigit = (((int)value) - (number * 1000000)) / 100000;
            }
            else
            {
                // Billions
                number = (int)(value / 1000000000);
                order = "billion";
                nextDigit = (int)(((long)value) - ((long)number * 1000000000)) / 100000000;
            }

            if (order == "")
            {
                return "" + number;
            }
            else
            {
                if (number > 60)
                {
                    if (nextDigit < 6)
                    {
                        return "Over " + number + " " + order;
                    }
                    else
                    {
                        return "Nearly " + (number + 1) + " " + order;
                    }
                }
                switch (nextDigit)
                {
                    case 0:
                        return "just over " + number + " " + order;
                    case 1:
                        return "over " + number + " " + order;
                    case 2:
                        return "well over " + number + " " + order;
                    case 3:
                        return "on the way to " + number + " and a half " + order;
                    case 4:
                        return "nearly " + number + " and a half " + order;
                    case 5:
                        return "around " + number + " and a half " + order;
                    case 6:
                        return "just over " + number + " and a half " + order;
                    case 7:
                        return "well over " + number + " and a half " + order;
                    case 8:
                        return "on the way to " + (number + 1) + " " + order;
                    case 9:
                        return "nearly " + (number + 1) + " " + order;
                    default:
                        return "around " + number + " " + order;
                }
            }

        }

        // Debug method to allow manual updating of the system
        public static void updateSystem(string system)
        {
            if (Cmdr != null)
            {
                Cmdr.StarSystem = system;
            }
        }

        // Logging
        private static bool enableDebugging = false;
        private static readonly string LOGFILE = Environment.GetEnvironmentVariable("AppData") + @"\EDDI\eddi.log";
        private static void logDebug(string data)
        {
            if (enableDebugging)
            {
                using (System.IO.StreamWriter file = new System.IO.StreamWriter(LOGFILE, true))
                {
                    file.WriteLine(DateTime.Now.ToString() + ": " + data);
                }
            }
        }
        private static void logInfo(string data)
        {
            using (System.IO.StreamWriter file = new System.IO.StreamWriter(LOGFILE, true))
            {
                file.WriteLine(DateTime.Now.ToString() + ": " + data);
            }
        }
        private static void logError(string data)
        {
            using (System.IO.StreamWriter file = new System.IO.StreamWriter(LOGFILE, true))
            {
                file.WriteLine(DateTime.Now.ToString() + ": " + data);
            }
        }
    }
}
