﻿using EliteDangerousDataDefinitions;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace EliteDangerousDataProviderService
{
    /// <summary>Access to EDDP data<summary>
    public class DataProviderService
    {
        public static StarSystem GetSystemData(string system, decimal? x, decimal?y, decimal? z)
        {
            if (system == null) { return null; }

            // X Y and Z are provided to us with 3dp of accuracy even though they are x/32; fix that here
            if (x.HasValue)
            {
                x = (Math.Round(x.Value * 32))/32;
            }
            if (y.HasValue)
            {
                y = (Math.Round(y.Value * 32)) / 32;
            }
            if (z.HasValue)
            {
                z = (Math.Round(z.Value * 32)) / 32;
            }

            using (var client = new WebClient())
            {
                string response;
                try
                {
                    response = client.DownloadString("http://api.eddp.co/systems/" + Uri.EscapeDataString(system));
                    //logInfo("Response is " + response);
                }
                catch (WebException wex)
                {
                    // No information found on this system, or some other issue.  Create a very basic response
                    response = @"{""name"":""" + system + @"""";
                    if (x.HasValue)
                    {
                        response = response = @", ""x"":" + x;
                    }
                    if (y.HasValue)
                    {
                        response = response = @", ""y"":" + y;
                    }
                    if (x.HasValue)
                    {
                        response = response = @", ""z"":" + z;
                    }
                    response = response + @", ""stations"":[]}";
                    logInfo("Generating dummy response " + response);
                }
                return StarSystemFromEDDP(response, x, y, z);
            }
        }

        public static StarSystem StarSystemFromEDDP(string data, decimal? x, decimal? y, decimal? z)
        {
            return StarSystemFromEDDP(JObject.Parse(data), x, y, z);
        }

        public static StarSystem StarSystemFromEDDP(dynamic json, decimal? x, decimal? y, decimal? z)
        {
            StarSystem StarSystem = new StarSystem();
            StarSystem.Name = (string)json["name"];
            if (json["updated_at"] != null)
            {
                // We have real data so populate the rest of the data
                StarSystem.Population = (long?)json["population"] == null ? 0 : (long?)json["population"];
                StarSystem.Allegiance = (string)json["allegiance"];
                StarSystem.Government = (string)json["government"];
                StarSystem.Faction = (string)json["faction"];
                StarSystem.PrimaryEconomy = (string)json["primary_economy"];
                StarSystem.State = (string)json["state"] == "None" ? null : (string)json["state"];
                StarSystem.Security = (string)json["security"];
                StarSystem.Power = (string)json["power"] == "None" ? null : (string)json["power"];
                StarSystem.PowerState = (string)json["power_state"];

                StarSystem.X = json["x"] == null ? x : (decimal?)json["x"];
                StarSystem.Y = json["y"] == null ? y : (decimal?)json["y"];
                StarSystem.Z = json["z"] == null ? y : (decimal?)json["z"];

                StarSystem.Stations = StationsFromEDDP(json);
            }

            return StarSystem;
        }

        public static List<Station> StationsFromEDDP(dynamic json)
        {
            List<Station> Stations = new List<Station>();

            if (json["stations"] != null)
            {
                foreach (dynamic station in json["stations"])
                {
                    Station Station = new Station();
                    Station.Name = (string)station["name"];

                    Station.Allegiance = (string)station["allegiance"];
                    Station.DistanceFromStar = (long?)station["distance_to_star"];
                    Station.HasRefuel = (bool?)station["has_refuel"];
                    Station.HasRearm = (bool?)station["has_rearm"];
                    Station.HasRepair = (bool?)station["has_repair"];
                    Station.HasOutfitting = (bool?)station["has_outfitting"];
                    Station.HasShipyard = (bool?)station["has_shipyard"];
                    Station.HasMarket = (bool?)station["has_market"];
                    Station.HasBlackMarket = (bool?)station["has_blackmarket"];

                    if (((string)station["type"]) != null)
                    {
                        if (StationModels.ContainsKey((string)station["type"]))
                        {
                            Station.Model = StationModels[(string)station["type"]];
                        }
                        else
                        {
                            LogError("Unknown station model " + ((string)station["type"]));
                        }
                    }

                    if (((string)station["max_landing_pad_size"]) != null)
                    {
                        if (LandingPads.ContainsKey((string)station["max_landing_pad_size"]))
                        {
                            Station.LargestShip = LandingPads[(string)station["max_landing_pad_size"]];
                        }
                        else
                        {
                            LogError("Unknown landing pad size " + ((string)station["max_landing_pad_size"]));
                        }
                    }

                    Stations.Add(Station);
                }
            }
            return Stations;
        }

        private static Dictionary<string, ShipSize> LandingPads = new Dictionary<string, ShipSize>()
        {
            { "S", ShipSize.Small },
            { "M", ShipSize.Medium },
            { "L", ShipSize.Large },
            { "H", ShipSize.Huge },
        };

        private static Dictionary<string, StationModel> StationModels = new Dictionary<string, StationModel>()
        {
            { "Civilian Outpost", StationModel.CivilianOutpost },
            { "Commercial Outpost", StationModel.CommercialOutpost },
            { "Coriolis Starport", StationModel.CoriolisStarport },
            { "Industrial Outpost", StationModel.IndustrialOutpost },
            { "Military Outpost", StationModel.MilitaryOutpost },
            { "Mining Outpost", StationModel.MiningOutpost },
            { "Ocellus Starport", StationModel.OcellusStarport },
            { "Orbis Starport", StationModel.OrbisStarport },
            { "Planetary Outpost", StationModel.PlanetaryOutpost },
            { "Planetary Port", StationModel.PlanetaryPort },
            { "Planetary Settlement", StationModel.PlanetarySettlement },
            { "Scientific Outpost", StationModel.ScientificOutpost},
            { "Unknown Outpost", StationModel.UnknownOutpost},
            { "Unknown Planetary", StationModel.UnknownPlanetary},
            { "Unknown Starport", StationModel.UnknownStarport},
            { "Unsanctioned Outpost", StationModel.UnsanctionedOutpost},
        };

        static DataProviderService()
        {
            // We need to not use an expect header as it causes problems when sending data to a REST service
            var profileUri = new Uri("http://api.eddp.co/profile");
            var profileServicePoint = ServicePointManager.FindServicePoint(profileUri);
            profileServicePoint.Expect100Continue = false;
            var errorUri = new Uri("http://api.eddp.co/error");
            var errorServicePoint = ServicePointManager.FindServicePoint(errorUri);
            errorServicePoint.Expect100Continue = false;
        }

        public static void LogProfile(string profile)
        {
            using (var client = new WebClient())
            {
                try
                {
                    client.UploadString(@"http://api.eddp.co/profile", profile);
                }
                catch (WebException wex)
                {
                    LogException(wex);
                }
            }
        }

        public static void LogException(Exception ex)
        {
            LogError(ex.ToString());
        }

        public static void LogError(String error)
        {
            using (var client = new WebClient())
            {
                try
                {
                    client.UploadString(@"http://api.eddp.co/error", error.ToString());
                }
                catch {}
            }
        }

        // Logging
        private static bool enableDebugging = false;
        private static readonly string LOGFILE = Environment.GetEnvironmentVariable("AppData") + @"\EDDI\eddi.log";
        private static void debug(string data)
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
