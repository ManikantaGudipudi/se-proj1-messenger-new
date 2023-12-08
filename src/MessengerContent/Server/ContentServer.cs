﻿/******************************************************************************
* Filename    = ContentServer.cs
*
* Author      = Manikanta Gudipudi
*
* Product     = Messenger
* 
* Project     = MessengerContent
*
* Description = file to obtain the files and chat messages from the server and 
*               pass them on to clients after processing.
*****************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Messenger.Client;
using MessengerContent.Client;
using MessengerContent.DataModels;
using MessengerContent.Enums;
using MessengerNetworking;
using MessengerNetworking.Communicator;
using MessengerNetworking.NotificationHandler;
using MessengerNetworking.Serializer;
using MessengerNetworking.Factory;
using TraceLogger;

namespace MessengerContent.Server
{
    public class ContentServer : IContentServer
    {
        private static readonly object s_lock = new();
        private readonly INotificationHandler _notificationHandler;
        private ChatServer _chatServer;
        private ContentDataBase _contentDatabase;
        private FileServer _fileServer;
        private IContentSerializer _serializer;
        private List<IMessageListener> _subscribers;

        public ContentServer()
        {
            _subscribers = new List<IMessageListener>();
            Communicator = CommunicationFactory.GetCommunicator(false);
            _contentDatabase = new ContentDataBase();
            _notificationHandler = new ContentServerNotificationHandler(this);
            _fileServer = new FileServer(_contentDatabase);
            _chatServer = new ChatServer(_contentDatabase);
            _serializer = new ContentSerializer();
            Communicator.Subscribe("Content", _notificationHandler);
        }

        /// <summary>
        /// Get and Set Communicator set auto for both tests and running
        /// </summary>
        public ICommunicator Communicator { get; set; }

        /// <inheritdoc />
        public void ServerSubscribe(IMessageListener subscriber)
        {
            _subscribers.Add(subscriber);
        }

        /// <inheritdoc />
        public List<ChatThread> GetAllMessages()
        {
            lock (s_lock)
            {
                return _chatServer.GetMessages();
            }
        }
        /// <summary>
        /// Send all the messages in the Database to Client
        /// </summary>
        public void SendAllMessagesToClient(int userId)
        {
            string allMessagesSerialized = _serializer.Serialize(GetAllMessages());
            Communicator.Send(allMessagesSerialized, "Content", userId.ToString());
        }


        /// <summary>
        /// Receives data from ContentServerNotificationHandler and processes it
        /// </summary>
        /// <param name="data"></param>
        public void Receive(string data)
        {
            ChatData messageData;
            // Try deserializing the data if error then do nothing and return.
            try
            {
                messageData = _serializer.Deserialize<ChatData>(data);
            }
            catch (Exception e)
            {
                Logger.Log($"[ContentServer] Exception occured while deserialsing data, Exception: {e}", LogLevel.WARNING);
                return;
            }

            ChatData receivedMessageData;
            Logger.Log("[ContentServer] Received MessageData from ContentServerNotificationHandler", LogLevel.INFO);

            // lock to prevent multiple threads from modifying the messages at once
            lock (s_lock)
            {
                switch (messageData.Type)
                {
                    case MessageType.Chat:
                        Logger.Log("[ContentServer] MessageType is Chat, Calling ChatServer.Receive()", LogLevel.INFO);
                        receivedMessageData = _chatServer.Receive(messageData);
                        break;

                    case MessageType.File:
                        Logger.Log("[ContentServer] MessageType is File, Calling FileServer.Receive()", LogLevel.INFO);
                        receivedMessageData = _fileServer.Receive(messageData);
                        break;

                    case MessageType.HistoryRequest:
                        Logger.Log("[ContentServer] MessageType is HistoryRequest, Calling ContentServer.SendAllMessagesToClient", LogLevel.INFO);
                        SendAllMessagesToClient(messageData.SenderID);
                        return;

                    default:
                        Logger.Log("[ContentServer] MessageType is Unknown", LogLevel.INFO);
                        return;
                }
            }

            // If this is null then something went wrong, probably message was not found.
            if (receivedMessageData == null)
            {
                Logger.Log("[ContentServer] Null Message received", LogLevel.WARNING);
                return;
            }

            try
            {
                // If Event is Download then send the file to client
                if (messageData.Event == MessageEvent.Download)
                {
                    Logger.Log("[ContentServer] MesseageEvent is Download, Sending File to client", LogLevel.INFO);
                    SendFile(receivedMessageData);
                }
                // Else send the message to all the receivers and notify the subscribers
                else
                {
                    Logger.Log("[ContentServer] Message received, Notifying subscribers", LogLevel.INFO);
                    Notify(receivedMessageData);
                    Logger.Log("[ContentServer] Sending message to clients", LogLevel.INFO);
                    Send(receivedMessageData);
                }
            }
            catch (Exception e)
            {
                Logger.Log($"[ContentServer] Something went wrong while sending message. Exception {e}", LogLevel.WARNING);
                return;
            }
            Logger.Log("[ContentServer] Message sent successfully", LogLevel.INFO);
        }

        /// <summary>
        /// Sends the message to clients.
        /// </summary>
        /// <param name="messageData"></param>
        public void Send(ChatData messageData)
        {
            string message = _serializer.Serialize(messageData);
            Communicator.Send(message, "Content", null);
        }

        /// <summary>
        /// Sends the file back to the requester.
        /// </summary>
        /// <param name="messageData"></param>
        public void SendFile(ChatData messageData)
        {
            string message = _serializer.Serialize(messageData);
            Communicator.Send(message, "Content", messageData.SenderID.ToString());
        }

        /// <summary>
        /// Notifies all the subscribed modules.
        /// </summary>
        /// <param name="receiveMessageData"></param>
        public void Notify(ReceiveChatData receiveMessageData)
        {
            foreach (IMessageListener subscriber in _subscribers)
            {
                _ = Task.Run(() => { subscriber.OnMessageReceived(receiveMessageData); });
            }
        }

        /// <summary>
        /// Resets the ContentServer, for Testing purpose
        /// </summary>
        public void Reset()
        {
            _subscribers = new List<IMessageListener>();
            _contentDatabase = new ContentDataBase();
            _fileServer = new FileServer(_contentDatabase);
            _chatServer = new ChatServer(_contentDatabase);
            _serializer = new ContentSerializer();
        }
    }
}
