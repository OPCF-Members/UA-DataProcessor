using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Opc.Ua.Cloud.Client.Models;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;

namespace Opc.Ua.Data.Processor
{
    public class ProductCarbonFootprintService
    {
        private readonly ADXDataService _adxDataService = new ADXDataService();
        private readonly DynamicsDataService _dynamicsDataService = new DynamicsDataService();
        private readonly HttpClient _webClient = new HttpClient()
        {
            BaseAddress = new Uri(Environment.GetEnvironmentVariable("UA_CLOUD_LIBRARY_URL")),
            DefaultRequestHeaders =
            {
                Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("UA_CLOUD_LIBRARY_USERNAME") + ":" + Environment.GetEnvironmentVariable("UA_CLOUD_LIBRARY_PASSWORD"))))
            }
        };

        public void GeneratePCFs()
        {
            // we have two production lines in the manufacturing ontologies production line simulation and they are connected like so:
            // assembly -> test -> packaging
            GeneratePCFForProductionLine("Munich", "48.1375", "11.575", 6);
            GeneratePCFForProductionLine("Seattle", "47.609722", "-122.333056", 10);
        }

        private void GeneratePCFForProductionLine(string productionLineName, string latitude, string longitude, int idealCycleTime)
        {
            try
            {
                // first of all, retrieve carbon intensity for the location of the production line
                CarbonIntensityQueryResult currentCarbonIntensity = WattTimeClient.GetCarbonIntensity(latitude, longitude).GetAwaiter().GetResult();
                if ((currentCarbonIntensity != null) && (currentCarbonIntensity.data.Length > 0))
                {
                    // check if a new product was produced (last machine in the production line, i.e. packaging, is in state 2 ("done") with a passed QA)
                    // and get the products serial number and energy consumption at that time
                    ConcurrentDictionary<string, object> latestProductProduced = ADXQueryForSpecificValue("packaging", productionLineName, "Status", 2);
                    if ((latestProductProduced != null) && (latestProductProduced.Count > 0))
                    {
                        ConcurrentDictionary<string, object> serialNumberResult = ADXQueryForSpecificTime("packaging", productionLineName, "ProductSerialNumber", ((DateTime)latestProductProduced["Timestamp"]).ToString("yyyy-MM-dd HH:mm:ss"), idealCycleTime);
                        double serialNumber = (double)serialNumberResult["OPCUANodeValue"];

                        ConcurrentDictionary<string, object> timeItWasProducedPackaging = ADXQueryForSpecificValue("packaging", productionLineName, "ProductSerialNumber", serialNumber);
                        ConcurrentDictionary<string, object> energyPackaging = ADXQueryForSpecificTime("packaging", productionLineName, "EnergyConsumption", ((DateTime)timeItWasProducedPackaging["Timestamp"]).ToString("yyyy-MM-dd HH:mm:ss"), idealCycleTime);

                        // check each other machine for the time when the product with this serial number was in the machine and get its energy comsumption at that time
                        ConcurrentDictionary<string, object> timeItWasProducedTest = ADXQueryForSpecificValue("test", productionLineName, "ProductSerialNumber", serialNumber);
                        ConcurrentDictionary<string, object> energyTest = ADXQueryForSpecificTime("test", productionLineName, "EnergyConsumption", ((DateTime)timeItWasProducedTest["Timestamp"]).ToString("yyyy-MM-dd HH:mm:ss"), idealCycleTime);

                        ConcurrentDictionary<string, object> timeItWasProducedAssembly = ADXQueryForSpecificValue("assembly", productionLineName, "ProductSerialNumber", serialNumber);
                        ConcurrentDictionary<string, object> energyAssembly = ADXQueryForSpecificTime("assembly", productionLineName, "EnergyConsumption", ((DateTime)timeItWasProducedAssembly["Timestamp"]).ToString("yyyy-MM-dd HH:mm:ss"), idealCycleTime);

                        // calculate the total energy consumption for the product by summing up all the machines' energy consumptions (in Ws), divide by 3600 to get seconds and multiply by the ideal cycle time (which is in seconds)
                        double energyTotal = ((double)energyAssembly["OPCUANodeValue"] + (double)energyTest["OPCUANodeValue"] + (double)energyPackaging["OPCUANodeValue"]) / 3600 * idealCycleTime;

                        // we set scope 1 emissions to 0
                        float scope1Emissions = 0.0f;

                        // finally calculate the scope 2 product carbon footprint by multiplying the full energy consumption by the current carbon intensity
                        float scope2Emissions = (float)energyTotal * currentCarbonIntensity.data[0].intensity.actual;

                        // we get scope 3 emissions from Dynamics as part of the Bill of Material (BoM)
                        float scope3Emissions = RetrieveScope3Emissions();

                        // finally calculate our PCF
                        float pcf = scope1Emissions + scope2Emissions + scope3Emissions;

                        // persist in cloud library
                        PersistInCloudLibrary(productionLineName, serialNumber, pcf);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("GeneratePCFForProductionLine: " + ex.Message);
            }
        }

        private void PersistInCloudLibrary(string productionLineName, double serialNumber, float pcf)
        {
            string aasName = "CarbonFootprintAAS_" + productionLineName + "_" + serialNumber.ToString();
            string pathToAAS = "./NodeSets/CarbonFootprintAAS_" + aasName + ".NodeSet2.xml";

            // write the values to a JSON file
            Dictionary<string, string> values = new()
            {
                { "i=9", "GHG Protocol" },              // PCFCalculationMethod
                { "i=10", pcf.ToString() },             // PCFCO2eq
                { "i=11", serialNumber.ToString() },    // PCFReferenceValueForCalculation
                { "i=12", "gCO2" },                     // PCFQuantityOfMeasureForCalculation
                { "i=14", "Scope 2 & 3 Emissions" },    // ExplanatoryStatement
                { "i=19", productionLineName },         // PCFGoodsAddressHandover.CityTown
                { "i=21", DateTime.UtcNow.ToString() }  // PublicationDate
            };

            string valuesJson = JsonConvert.SerializeObject(values);

            UANameSpace nameSpace = new();
            nameSpace.Title = aasName;
            nameSpace.License = "MIT";
            nameSpace.CopyrightText = "OPC Foundation";
            nameSpace.Description = "Sample PCF for Digital Twin Consortium production line simulation";
            nameSpace.Nodeset.NodesetXml = File.ReadAllText("./CarbonFootprintAAS.NodeSet2.xml").Replace("CarbonFootprintAAS", aasName);

            string body = JsonConvert.SerializeObject(nameSpace);

            Uri address = new Uri(_webClient.BaseAddress.AbsoluteUri + "infomodel/upload?overwrite=true");
            HttpResponseMessage response = _webClient.Send(new HttpRequestMessage(HttpMethod.Put, address) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

            Console.WriteLine("Response: " + response.StatusCode.ToString());
            string responseStr = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Console.WriteLine(responseStr);

            // TODO: upload values as well
        }

        private float RetrieveScope3Emissions()
        {
            try
            {
                DynamicsQueryResponse response = _dynamicsDataService.RunDynamicsQuery(new DynamicsQuery() {
                    tracingDirection = "Backward",
                    company = Environment.GetEnvironmentVariable("DYNAMICS_COMPANY_NAME"),
                    itemNumber = Environment.GetEnvironmentVariable("DYNAMICS_PRODUCT_NAME"),
                    serialNumber = Environment.GetEnvironmentVariable("DYNAMICS_BATCH_NAME"),
                    shouldIncludeEvents = true
                }).GetAwaiter().GetResult();

                if (response != null)
                {
                    return FindPcf(response.root);
                }
                else
                {
                    return 0.0f;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("RetrieveScope3Emissions: " + ex.Message);
                return 0.0f;
            }
        }

        private float FindPcf(ErpNode node)
        {
            if (node.events != null)
            {
                foreach (ErpEvent erpEvent in node.events)
                {
                    if (erpEvent.productTransactions != null)
                    {
                        foreach (ErpTransaction transaction in erpEvent.productTransactions)
                        {
                            if ((transaction.details != null) && (transaction.details.First != null) && ((JProperty)transaction.details.First).Name.ToLowerInvariant() == "pcf")
                            {
                                return float.Parse(((JProperty)transaction.details.First).Value.ToString()) / transaction.quantity;
                            }
                        }
                    }
                }
            }

            if (node.next != null)
            {
                foreach (ErpNode nextNode in node.next)
                {
                    float pcf = FindPcf(nextNode);
                    if (pcf != 0.0f)
                    {
                        return pcf;
                    }
                }
            }

            // not found
            return 0.0f;
        }

        private ConcurrentDictionary<string, object> ADXQueryForSpecificValue(string stationName, string productionLineName, string valueToQuery, double desiredValue)
        {
            string query = "opcua_metadata_lkv\r\n"
                         + "| where Name contains \"" + stationName + "\"\r\n"
                         + "| where Name contains \"" + productionLineName + "\"\r\n"
                         + "| join kind = inner(opcua_telemetry\r\n"
                         + "    | where Name == \"" + valueToQuery + "\"\r\n"
                         + "    | where Timestamp > now(- 1h)\r\n"
                         + ") on DataSetWriterID\r\n"
                         + "| distinct Timestamp, OPCUANodeValue = todouble(Value)\r\n"
                         + "| sort by Timestamp desc";

            ConcurrentDictionary<string, object> values = new ConcurrentDictionary<string, object>();
            _adxDataService.RunADXQuery(query, values);

            return values;
        }

        private ConcurrentDictionary<string, object> ADXQueryForSpecificTime(string stationName, string productionLineName, string valueToQuery, string timeToQuery, int idealCycleTime)
        {
            string query = "opcua_metadata_lkv\r\n"
                         + "| where Name contains \"" + stationName + "\"\r\n"
                         + "| where Name contains \"" + productionLineName + "\"\r\n"
                         + "| join kind = inner(opcua_telemetry\r\n"
                         + "    | where Name == \"" + valueToQuery + "\"\r\n"
                         + "    | where Timestamp > now(- 1h)\r\n"
                         + ") on DataSetWriterID\r\n"
                         + "| distinct Timestamp, OPCUANodeValue = todouble(Value)\r\n"
                         + "| where around(Timestamp, datetime(" + timeToQuery + "), " + idealCycleTime.ToString() + "s)\r\n"
                         + "| sort by Timestamp desc";

            ConcurrentDictionary<string, object> values = new ConcurrentDictionary<string, object>();
            _adxDataService.RunADXQuery(query, values);

            return values;
        }
    }
}
