using IoTHubTrigger = Microsoft.Azure.WebJobs.EventHubTriggerAttribute;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventHubs;
using System.Text;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using Microsoft.Azure.Devices;
using System.Threading.Tasks;

// Statistics graph
using System;
using System.Data;
using System.Collections.Generic;
using System.Diagnostics;

namespace Company.Function
{

    public class Root
    {
        public int messageType { get; set; }
        public double distanceMeasured { get; set; }
    }

    public class ValuesByHour
    {
        public List<int> numOfNotificationsByHour { get; set; }
    }

    public class IotHubTriggerCSharp1
    {

        const string eventHubName = "omitted";
        const string databaseConnectionString = "omitted";
        const string ioTHubConnectionString = "omitted=";
        const string distanceMeasurerID = "1";
        const string androidAppsID = "Androids";
        
        static ServiceClient serviceClient;        
        private static HttpClient client = new HttpClient();

        public ILogger logger;
        
        [FunctionName("IotHubTriggerCSharp1")]
        public void Run([IoTHubTrigger("messages/events", Connection = "EventHubConnectionString")]string msg, ILogger log)
        {
            logger = log;

            logger.LogInformation($"C# IoT Hub trigger function processed a message: {msg}");

            if (!string.IsNullOrEmpty(msg))
            {
                Root myDeserializedClass = JsonConvert.DeserializeObject<Root>(msg);
                
                switch(myDeserializedClass.messageType){
                    case 1: // Distance measurer is updating the windowIsOpen flag in the database
                        logger.LogInformation($"GOT 1"); 
                        bool flagValue = myDeserializedClass.distanceMeasured > 0.5;
                        logger.LogInformation($"The flag is: {flagValue}");
                        ForwardFlagToDatabase(flagValue);

                        break;

                    case 2: // Distance measurer is sending a notification regarding the length towards opened window to the mobile phone
                        logger.LogInformation($"GOT 2");
                        logger.LogInformation($"The distance to opened is: {myDeserializedClass.distanceMeasured}");
                        CloudToDeviceMessage(androidAppsID, 2, myDeserializedClass.distanceMeasured);

                        break;

                    case 3: // Distance measurer is sending a notification regarding the length towards closed window to the mobile phone
                        logger.LogInformation($"GOT 3");
                        logger.LogInformation($"The distance to closed is: {myDeserializedClass.distanceMeasured}");
                        CloudToDeviceMessage(androidAppsID, 3, myDeserializedClass.distanceMeasured);

                        break;
                        
                    case 4: // The mobile phone is instructing the distance measurer to measure the distance towards the open window
                        logger.LogInformation($"GOT 4");
                        logger.LogInformation($"Mobile phone is requesting distance towards open");
                        CloudToDeviceMessage(distanceMeasurerID, 4, myDeserializedClass.distanceMeasured);
                        break;
                        
                    case 5: // The mobile phone is instructing the distance measurer to measure the distance towards the closed window
                        logger.LogInformation($"GOT 5");
                        logger.LogInformation($"Mobile phone is requesting distance towards closed");
                        CloudToDeviceMessage(distanceMeasurerID, 5, myDeserializedClass.distanceMeasured);

                        break;

                    case 7: // The mobile phone is requesting data about notification times
                        logger.LogInformation($"GOT 7");
                        logger.LogInformation($"Mobile phone is requesting data for visualisation");

                        CloudToDeviceVisualisationMessage();

                        break;
                }

            } else{
                logger.LogInformation("The received message is empty");
            }
        }

        private void CloudToDeviceVisualisationMessage(){

            // Query for all times of notifying in the last 24 hours (in UTC+1 time)
            List<DateTime> datetimeResults = GetDatabaseNotificationTimeEntries();

            List<int> numOfOccurrences = ProcessDatabaseResults(datetimeResults);

            string jsonString = CreateStringFromObject(numOfOccurrences);

            logger.LogInformation("Sending message: " + jsonString);

            SendCloudToDeviceVisualisationMessageAsync(jsonString).Wait();
        }

        private static async Task SendCloudToDeviceVisualisationMessageAsync(string jsonString)
        {
            var commandMessage = new Message(Encoding.ASCII.GetBytes(jsonString));
            serviceClient = ServiceClient.CreateFromConnectionString(ioTHubConnectionString);

            await serviceClient.SendAsync(androidAppsID, commandMessage);
        }

        private static List<int> ProcessDatabaseResults(List<DateTime> datetimeResults)
        {
             // For all values in datetimeResults aggregate those so you get the number of entries for every hour in a day
            // Initialize a List<int> to store occurrences for each hour
            List<int> numOfOccurrences = new(24);

            // Initialize the list with zeros for each hour
            for (int i = 0; i < 24; i++)
            {
                numOfOccurrences.Add(0);
            }

            // Grouping datetimeResults by hour and counting occurrences
            foreach (DateTime dt in datetimeResults)
            {
                if(dt.Day == DateTime.UtcNow.Day){
                    numOfOccurrences[dt.Hour]++;
                }
            }

            return numOfOccurrences;
        }

        private static string CreateStringFromObject(List<int> numOfOccurrences)
        {
            ValuesByHour messageToSend = new()
            {
                numOfNotificationsByHour = numOfOccurrences
            };

            return JsonConvert.SerializeObject(messageToSend);
        }

        private List<DateTime> GetDatabaseNotificationTimeEntries(){
            using SqlConnection connection = new(databaseConnectionString);
            connection.Open();

            string datetimeResultsQuery = "SELECT datetimeOfNotifying FROM [dbo].[TimesOfNotifying] WHERE datetimeOfNotifying >= DATEADD(HOUR, -23, GETUTCDATE());";
            // the query is designed for UTC+1 timezone
            
            List<DateTime> datetimeResults = new();
            
            logger.LogInformation("Getting the values");
            using (SqlCommand command = new(datetimeResultsQuery, connection))
            {
                //logger.LogInformation("command is set");
                using SqlDataReader reader = command.ExecuteReader();
                //logger.LogInformation("reader was executed");
                while (reader.Read())
                {
                    datetimeResults.Add(Convert.ToDateTime(reader.GetValue(0)));
                }
            }

            connection.Close();

            return datetimeResults;
        }

        public void ForwardFlagToDatabase(bool flagValue){
            string query;
            if(flagValue){
                query = $"UPDATE [dbo].[WindowsAreOpen] SET windowIsOpen=1 WHERE ID=1;";
            }else{
                query = $"UPDATE [dbo].[WindowsAreOpen] SET windowIsOpen=0 WHERE ID=1;";
            }
            
            logger.LogInformation($"The query is: {query}");
            
            NonQueryDatabase(query);
        }

        public void NonQueryDatabase(string query){
            using SqlConnection connection = new(databaseConnectionString);
            connection.Open();

            logger.LogInformation($"Opened connection to database");

            using (SqlCommand command = new(query, connection))
            {
                var rows = command.ExecuteNonQuery();
                
                logger.LogInformation($"{rows} rows were updated in the database");
            }

            connection.Close();
        }

        public void CloudToDeviceMessage(string deviceId, int mesType, double distanceMes){
            logger.LogInformation("Send Cloud-to-Device message");
            serviceClient = ServiceClient.CreateFromConnectionString(ioTHubConnectionString);

            SendCloudToDeviceMessageAsync(deviceId, mesType, distanceMes).Wait();
        }
        
        private async Task SendCloudToDeviceMessageAsync(string deviceId, int mesType, double distMeasured)
        {
            Root messageToSend = new()
            {
                messageType = mesType,
                distanceMeasured = distMeasured
            };

            string jsonString = JsonConvert.SerializeObject(messageToSend);
            logger.LogInformation("Sending message: " + jsonString);
            var commandMessage = new Message(Encoding.ASCII.GetBytes(jsonString));
            await serviceClient.SendAsync(deviceId, commandMessage);
        }
    }
}
