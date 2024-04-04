#include <Arduino.h>
#include <AzIoTSasToken.h>
#include <SerialLogger.h>
#include <WiFi.h>
#include <az_core.h>
#include <azure_ca.h>
#include <ctime>
#include <json.hpp>
#include "WiFiClientSecure.h"

#include "PubSubClient.h"
#include "ArduinoJson.h"
using json = nlohmann::json;

// 
struct MessageStruct {
  int messageType;
  double distanceMeasured;
} RecievedMessageStruct;

long nextTime = 0;

float distanceToOpen = 6;
float distanceToClosed = 10;
bool isCalibrated = false;

int state = 0;

struct Calibration {
  bool calibratedOpenWindow = false;
  bool calibratedClosedWindow = false;
} CalibrationCheckStruct;

bool telemetryInserted = false;

/* Azure auth data */
char* deviceKey = "1eliEUZq00+5t8NVQkxh9a3l5+mQSP3DSAIoTGgo62s=";	 // Azure Primary key for device
const char* iotHubHost = "HUB-RUS-jbegovic21.azure-devices.net";		 //[Azure IoT host name].azure-devices.net
const int tokenDuration = 60;

const char* deviceId = "1";  // Device ID as specified in the list of devices on IoT Hub

/* MQTT data for IoT Hub connection */
const char* mqttBroker = iotHubHost;  // MQTT host = IoT Hub link
const int mqttPort = AZ_IOT_DEFAULT_MQTT_CONNECT_PORT;	// Secure MQTT port
const char* mqttC2DTopic = AZ_IOT_HUB_CLIENT_C2D_SUBSCRIBE_TOPIC;	// Topic where we can receive cloud to device messages

// These three are just buffers - actual clientID/username/password is generated
// using the SDK functions in initIoTHub()
char mqttClientId[128];
char mqttUsername[128];
char mqttPasswordBuffer[200];
char publishTopic[200];

/* Auth token requirements */

uint8_t sasSignatureBuffer[256];  // Make sure it's of correct size, it will just freeze otherwise :/

az_iot_hub_client client;
AzIoTSasToken sasToken(
	&client, az_span_create_from_str(deviceKey),
	AZ_SPAN_FROM_BUFFER(sasSignatureBuffer),
	AZ_SPAN_FROM_BUFFER(
		mqttPasswordBuffer));	 // Authentication token for our specific device

/* MY variable definitions */
const int trigPIN = 26;
const int echoPIN = 35;

float distanceMeasurement = 0; 

/* WiFi things */

WiFiClientSecure wifiClient;
PubSubClient mqttClient(wifiClient);

const char* ssid = "Galaxy A21s12E2";
const char* pass = "S123456789";
short timeoutCounter = 0;


//JSON PARSING ZA JSON U OBLIKU STRINGA
MessageStruct JsonParse(String JsonString) {
  json JsonData = json::parse(JsonString);
  RecievedMessageStruct.messageType = JsonData["messageType"];
  RecievedMessageStruct.distanceMeasured = JsonData["distanceMeasured"];

  return RecievedMessageStruct;
}

//NOVOSEL FUNKCIJA ZA KALIBRACIJU, OVO JE INICIJALIZACIJA A DEKLARACIJA JE DOLJE
void calibrate(int orderNumber);
void sendTelemetryData(int orderNumber);

// Jakov funkcija
float getDistance() { // Do not block using delay(), instead check if enough time has passed between two calls using millis()

    // Clear the trigPin by setting it LOW:
    digitalWrite(trigPIN, LOW);
    delayMicroseconds(5);

    // Trigger the sensor by setting the trigPin high for 10 microseconds:
    digitalWrite(trigPIN, HIGH);
    delayMicroseconds(10);
    digitalWrite(trigPIN, LOW);

    // Read the echoPin, pulseIn() returns the duration (length of the pulse) in microseconds and from that value calculate the distance:
    distanceMeasurement = pulseIn(echoPIN, HIGH);
    distanceMeasurement *= 0.034 / 2;

    return distanceMeasurement;
}

void setupWiFi() {
	Logger.Info("Connecting to WiFi");

	//wifiClient.setCACert((const char*)ca_pem); // We are using TLS to secure the connection, therefore we need to supply a certificate (in the SDK)
  wifiClient.setInsecure();

	WiFi.mode(WIFI_STA);
	WiFi.begin(ssid, pass);

	while (WiFi.status() != WL_CONNECTED) { // Wait until we connect...
		Serial.print(".");
		delay(500);

		timeoutCounter++;
		if (timeoutCounter >= 20) ESP.restart(); // Or restart if we waited for too long, not much else can you do
	}

	Logger.Info("WiFi connected");
}

// Use pool pool.ntp.org to get the current time
// Define a date on 1.1.2023. and wait until the current time has the same year (by default it's 1.1.1970.)
void initializeTime() {	 // MANDATORY or SAS tokens won't generate
  configTime(0,0, "pool.ntp.org", "time.nist.gov");

  time_t now = time(NULL);
  tm tm{};
  tm.tm_year = 2024;

  while(now < mktime(&tm)){
    delay(500);
    Serial.print(".");
    now = time(NULL);
  }
}


// MQTT is a publish-subscribe based, therefore a callback function is called whenever something is published on a topic that device is subscribed to
// It's also a binary-safe protocol, therefore instead of transfering text, bytes are transfered and they aren't null terminated - so we need to add \0 to terminate the string
void callback(char *topic, byte *payload, unsigned int length) { 
  payload[length] = '\0';
  String message = String((char*)payload);
  
  Logger.Info(message);

  MessageStruct Result = JsonParse(message);
  
  Logger.Info("messageType: " + String(Result.messageType) + " distanceMeasured: " + String(Result.distanceMeasured));

  // if the messageType is 4 Dasduino measures distance to open window
  // if the messageType is 5 Dasduino measures distance to closed window

  switch (Result.messageType) {
    case 4: calibrate(1); sendTelemetryData(2); break;
    case 5: calibrate(2); sendTelemetryData(3); break;
  }

}

void connectMQTT() {
  mqttClient.setBufferSize(1024);

  sasToken.Generate(tokenDuration);

  mqttClient.setServer(mqttBroker, mqttPort);
  mqttClient.setCallback(callback);
}

// in the case of disconnecting
void mqttReconnect() {
  while(!mqttClient.connected()){
    const char* mqttPassword = (const char*)az_span_ptr(sasToken.Get()); // az span is a tenth String implementation

    if(mqttClient.connect(mqttClientId, mqttUsername, mqttPassword)){
      mqttClient.subscribe(mqttC2DTopic);
    }else{
      Logger.Error("Trying again in 5 sec");
      delay(5000);
    }
  }
}

String getTelemetryData(int orderNumber) { // Get the data and pack it in a JSON message
  StaticJsonDocument<128> doc; // Create a JSON document we'll reuse to serialize our data into JSON
  String output = "";

  float distance = getDistance();

  Logger.Info(String(distance));

  switch (orderNumber) {

    case 1:

      if (isCalibrated) {
        if (distanceToOpen < distanceToClosed) {
          if (distance > (distanceToClosed - 1) && state == 1) {
            //Window is closed
            Logger.Info("Closed");

            doc["messageType"] = 1;
            doc["distanceMeasured"] = 0;
            state = 0;
            telemetryInserted = true;
          }
          else if (distance < (distanceToOpen + 1) && state == 0) {
            //Window is opened
            Logger.Info("Open");

            doc["messageType"] = 1;
            doc["distanceMeasured"] = 1;
            state = 1;
            telemetryInserted = true;
          }
          else if (!(distance < (distanceToOpen + 1)) && state == 1) {
            //Distance between open and closed = window is closed
            Logger.Info("Between");

            doc["messageType"] = 1;
            doc["distanceMeasured"] = 0;
            state = 0;
            telemetryInserted = true;
          }
        } else {
          if (distance < (distanceToClosed + 1) && state == 1) {
            //Window is closed
            Logger.Info("Closed");

            doc["messageType"] = 1;
            doc["distanceMeasured"] = 0;
            state = 0;
            telemetryInserted = true;
          }
          else if (distance > (distanceToOpen - 1) && state == 0) {
            //Window is open
            Logger.Info("Open");

            doc["messageType"] = 1;
            doc["distanceMeasured"] = 1;
            state = 1;
            telemetryInserted = true;
          }
          else if (!(distance > (distanceToOpen - 1)) && state == 1) {
            //Distance between open and closed = window is closed

            Logger.Info("Izmedju");
            doc["messageType"] = 1;
            doc["distanceMeasured"] = 0;
            state = 0;
            telemetryInserted = true;
            
          }
        }
      }

      break;

    case 2:

      doc["messageType"] = 2;
      doc["distanceMeasured"] = distanceToOpen;
      telemetryInserted = true;
      break;

    case 3:
    
      doc["messageType"] = 3;
      doc["distanceMeasured"] = distanceToClosed;
      telemetryInserted = true;
      break;
  }

	serializeJson(doc, output);

	Logger.Info(output);

  return output;
}

void sendTelemetryData(int orderNumber) {
  String telemetryData = "";
  telemetryInserted = false;

  telemetryData = getTelemetryData(orderNumber);

  bool result = false;

  if (telemetryInserted) {
    Logger.Info("Sending");
    result = mqttClient.publish(publishTopic, telemetryData.c_str());

    if(result){
        Logger.Info("Successful publish");
    }else{
        Logger.Info("Unsuccessful publish");
    }
  }else{    
    Logger.Info("Not sending");
  }

	
}

void calibrate(int orderNumber){
  switch (orderNumber) {
    case 1: distanceToOpen = getDistance(); CalibrationCheckStruct.calibratedOpenWindow = true; break;
    case 2: distanceToClosed = getDistance(); CalibrationCheckStruct.calibratedClosedWindow = true; break;
  }
  
  isCalibrated = CalibrationCheckStruct.calibratedOpenWindow && CalibrationCheckStruct.calibratedClosedWindow;
}

bool initIoTHub() {
  az_iot_hub_client_options options = az_iot_hub_client_options_default(); // Get a default instance of IoT Hub client options

  if (az_result_failed(az_iot_hub_client_init( // Create an instnace of IoT Hub client for our IoT Hub's host and the current device
          &client,
          az_span_create((unsigned char *)iotHubHost, strlen(iotHubHost)),
          az_span_create((unsigned char *)deviceId, strlen(deviceId)),
          &options)))
  {
    Logger.Error("Failed initializing Azure IoT Hub client");
    return false;
  }

  size_t client_id_length;
  if (az_result_failed(az_iot_hub_client_get_client_id(
          &client, mqttClientId, sizeof(mqttClientId) - 1, &client_id_length))) // Get the actual client ID (not our internal ID) for the device
  {
    Logger.Error("Failed getting client id");
    return false;
  }

  size_t mqttUsernameSize;
  if (az_result_failed(az_iot_hub_client_get_user_name(
          &client, mqttUsername, sizeof(mqttUsername), &mqttUsernameSize))) // Get the MQTT username for our device
  {
    Logger.Error("Failed to get MQTT username ");
    return false;
  }

  Logger.Info("Great success");
  Logger.Info("Client ID: " + String(mqttClientId));
  Logger.Info("Username: " + String(mqttUsername));

  return true;
}

void setup() {
  setupWiFi();
  initializeTime();

  if (initIoTHub()) {
    connectMQTT();
    mqttReconnect();
  }

  az_result res = az_iot_hub_client_telemetry_get_publish_topic(&client, NULL, publishTopic, 200, NULL );
  // The receive topic isn't hardcoded and depends on chosen properties, therefore we need to use 
  // az_iot_hub_client_telemetry_get_publish_topic()
  
  // Setup ultrasonic sensor
  pinMode(trigPIN, OUTPUT);
  pinMode(echoPIN, INPUT);

  Logger.Info("Setup done");
}


void loop() { 
  // No blocking in the loop, constantly check if we are connected and gather the data if necessary
  if(!mqttClient.connected()) mqttReconnect();

  mqttClient.loop();

  if(nextTime <= millis()){
    sendTelemetryData(1);

    nextTime = millis() + 10000; // + 10 seconds
  } 
  
}