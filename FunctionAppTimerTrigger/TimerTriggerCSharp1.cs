using System;
using System.Data.SqlClient;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.Azure.Devices;
using Newtonsoft.Json;
using System.Text;

// Firebase
using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using System.IO;

// Statistics graph
using System.Data;

// For attributes
using Microsoft.Azure.WebJobs.Extensions;
using System.Threading;

namespace Company.Function
{

    public class NotificationToSend
    {
        public string type { get; set; }
        public string project_id { get; set; }
        public string private_key_id { get; set; }
        public string private_key { get; set; }
        public string client_email { get; set; }
        public string client_id { get; set; }
        public string auth_uri { get; set; }
        public string token_uri { get; set; }
        public string auth_provider_x509_cert_url { get; set; }
        public string client_x509_cert_url { get; set; }
        public string universe_domain { get; set; }
    }


    public class TimerTriggerCSharp1
    {
        // SQL Database
        private const string databaseConnectionString = "omitted";

        ILogger log;

        [FunctionName("TimerTriggerCSharp1")]
        public void Run([TimerTrigger("0 */2 * * * *")]TimerInfo myTimer, ILogger logger, Microsoft.Azure.WebJobs.ExecutionContext context)
        {

            log = logger;
            log.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");

            // Check if the connection string is null or empty
            if (string.IsNullOrEmpty(databaseConnectionString))
            {
                log.LogInformation("Azure SQL Database connection string is missing or empty.");
                return;
            }
            
            string queryForWindow = "SELECT windowIsOpen FROM WindowsAreOpen WHERE ID=1;";
            string queryForOutdoorQuality = "SELECT value FROM [dbo].[AirQualityReadings] WHERE ID=2;";
            string queryForIndoorQuality = "SELECT value FROM [dbo].[AirQualityReadings] WHERE ID=1;";

            using SqlConnection connection = new(databaseConnectionString);

            connection.Open();

            bool windowIsOpen = QueryDatabaseBoolValue(connection, queryForWindow);
            log.LogInformation($"Window is open: {windowIsOpen}");

            float qualityOutdoors = QueryDatabaseFloatValue(connection, queryForOutdoorQuality);
            log.LogInformation($"Outdoor quality: {qualityOutdoors}");
            
            float qualityIndoors = QueryDatabaseFloatValue(connection, queryForIndoorQuality);
            log.LogInformation($"Indoor quality: {qualityIndoors}");

            // send message to Android to open window
            if(qualityIndoors > 1200 && !windowIsOpen){ // if higher than 1200 ppm and window is closed
                // Update the database
                SqlCommand insertDatetimeOfNotifyingCommand = CreateInputCommand(connection);
                InsertIntoDatabase(insertDatetimeOfNotifyingCommand);
            }

            connection.Close();
            log.LogInformation($"Closed connection to database");

            // if block detached from other so the database connection always closes
            if(qualityIndoors > 1200 && !windowIsOpen){ 
                log.LogInformation("Sending notification using Firebase");

                var defaultApp = FirebaseApp.Create(new AppOptions()
                {
                    Credential = GoogleCredential.FromJson(CreateJsonForGoogleAuth()),
                });

                log.LogInformation(defaultApp.Name); // "[DEFAULT]"

                var message = new FirebaseAdmin.Messaging.Message()
                {
                    Notification = new Notification
                    {
                        Title = "Open the window",
                        Body = "Air quality in your room has dropped. Please open your window."
                    },
                    Topic = "Notifications"
                };
                log.LogInformation(message.ToString());

                var messaging = FirebaseMessaging.DefaultInstance;

                _ = CloudToDeviceMessageAsync(message, messaging);
            }
                
            log.LogInformation($"C# Timer trigger function completed at: {DateTime.Now}");
        }

        private async Task CloudToDeviceMessageAsync(FirebaseAdmin.Messaging.Message message, FirebaseMessaging messaging)
        {
            var result = await messaging.SendAsync(message);

            log.LogInformation(result); //projects/myapp/messages/2492588335721724324
        }

        private static bool QueryDatabaseBoolValue(SqlConnection connection, string queryForWindow){
            using SqlCommand command = new(queryForWindow, connection);
            object result = command.ExecuteScalar();

            return (bool)result;
        }

        private static float QueryDatabaseFloatValue(SqlConnection connection, string queryForWindow){
            using SqlCommand command = new(queryForWindow, connection);
            object result = command.ExecuteScalar();

            return (float)result;
        }

        private string CreateJsonForGoogleAuth()
        {
            NotificationToSend messageToSend = new()
            {
                type = "service_account",
                project_id = "rus-projekt",
                private_key_id = "edeaa9370cb3f8aba0bd5185ee59c05fb2134e4a",
                private_key = "omitted"
                client_email = "firebase-adminsdk-ck5uv@rus-projekt.iam.gserviceaccount.com",
                client_id = "101603156093799438868",
                auth_uri = "https://accounts.google.com/o/oauth2/auth",
                token_uri = "https://oauth2.googleapis.com/token",
                auth_provider_x509_cert_url = "https://www.googleapis.com/oauth2/v1/certs",
                client_x509_cert_url = "https://www.googleapis.com/robot/v1/metadata/x509/firebase-adminsdk-ck5uv%40rus-projekt.iam.gserviceaccount.com",
                universe_domain = "googleapis.com"
            };

            string jsonString = JsonConvert.SerializeObject(messageToSend);
            log.LogInformation(jsonString);
            return jsonString;
        } 

        private static SqlCommand CreateInputCommand(SqlConnection connection)
        {
            string inputCommand = "INSERT INTO [dbo].[TimesOfNotifying] VALUES(@insertSmallDatatype);";
            // Example: "INSERT INTO [dbo].[TimesOfNotifying] VALUES('2024-01-30 00:01');";

            SqlCommand command = new(inputCommand, connection);
            
            command.Parameters.Add(new SqlParameter("@insertSmallDatatype", SqlDbType.SmallDateTime) { Value = DateTime.UtcNow.AddHours(1) }); // the time needs to be UTC+1, independent of location of execution

            return command;
        }

        public void InsertIntoDatabase(SqlCommand commandToExecute){
            log.LogInformation("Inserting into database");

            using SqlCommand command = commandToExecute;
            var rows = command.ExecuteNonQuery();

            log.LogInformation($"{rows} rows were updated in the database");
        }

        
    }
}
