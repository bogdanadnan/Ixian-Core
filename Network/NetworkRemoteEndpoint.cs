﻿using Force.Crc32;
using IXICore.Inventory;
using IXICore.Meta;
using IXICore.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace IXICore.Network
{
    public class TimeSyncData
    {
        public long timeDifference = 0;
        public long remoteTime = 0;
        public long processedTime = 0;
    }

    public class RemoteEndpoint
    {
        class MessageHeader
        {
            public ProtocolMessageCode code;
            public uint dataLen;
            public uint dataChecksum;
            public byte[] legacyDataChecksum;
        }

        public string fullAddress = "127.0.0.1:0";
        public string address = "127.0.0.1";
        public int incomingPort = 0;

        public long connectionStartTime = 0;

        public long timeDifference = 0;
        public bool timeSyncComplete = false;

        public bool helloReceived = false;
        public ulong blockHeight = 0;

        protected long lastDataReceivedTime = 0;
        protected long lastDataSentTime = 0;

        public bool fullyStopped = false;

        public IPEndPoint remoteIP;
        public Socket clientSocket;
        public RemoteEndpointState state;

        // Maintain two threads for handling data receiving and sending
        protected Thread recvThread = null;
        protected Thread sendThread = null;
        protected Thread parseThread = null;

        public Presence presence = null;
        public PresenceAddress presenceAddress = null;

        protected bool running = false;

        // Maintain a list of subscribed event addresses with event type
        private Dictionary<NetworkEvents.Type, Cuckoo> subscribedFilters = new Dictionary<NetworkEvents.Type, Cuckoo>();

        // Maintain a queue of messages to send
        private List<QueueMessage> sendQueueMessagesHighPriority = new List<QueueMessage>();
        private List<QueueMessage> sendQueueMessagesNormalPriority = new List<QueueMessage>();
        private List<QueueMessage> sendQueueMessagesLowPriority = new List<QueueMessage>();

        // Maintain a queue of raw received data
        private List<QueueMessageRaw> recvRawQueueMessages = new List<QueueMessageRaw>();

        private byte[] socketReadBuffer = null;

        protected List<TimeSyncData> timeSyncs = new List<TimeSyncData>();

        protected bool enableSendTimeSyncMessages = true;

        private int messagesPerSecond = 0;
        private int lastMessagesPerSecond = 0;
        private DateTime lastMessageStatTime;

        private ThreadLiveCheck TLC;

        private List<InventoryItem> inventory = new List<InventoryItem>();
        private long inventoryLastSent = 0;

        public byte[] serverWalletAddress = null;
        public byte[] serverPubKey = null;

        public byte[] challenge = null;

        public int version = 5;

        protected void prepareSocket(Socket socket)
        {
            // The socket will linger for 3 seconds after 
            // Socket.Close is called.
            socket.LingerState = new LingerOption(true, 3);

            // Disable the Nagle Algorithm for this tcp socket.
            socket.NoDelay = true;

            socket.ReceiveTimeout = 120000;
            //socket.ReceiveBufferSize = 1024 * 64;
            //socket.SendBufferSize = 1024 * 64;
            socket.SendTimeout = 120000;

            socket.Blocking = true;
        }

        public void start(Socket socket = null)
        {
            if (fullyStopped)
            {
                Logging.error("Can't start a fully stopped RemoteEndpoint");
                return;
            }

            if (running)
            {
                return;
            }

            if (socket != null)
            {
                clientSocket = socket;
            }
            if (clientSocket == null)
            {
                Logging.error("Could not start NetworkRemoteEndpoint, socket is null");
                return;
            }

            prepareSocket(clientSocket);

            remoteIP = (IPEndPoint)clientSocket.RemoteEndPoint;
            address = remoteIP.Address.ToString();
            fullAddress = address + ":" + remoteIP.Port;
            presence = null;
            presenceAddress = null;

            connectionStartTime = Clock.getTimestamp();

            lock (subscribedFilters)
            {
                subscribedFilters.Clear();
            }

            lastDataReceivedTime = Clock.getTimestamp();
            lastDataSentTime = Clock.getTimestamp();

            state = RemoteEndpointState.Established;

            timeDifference = 0;
            timeSyncComplete = false;
            timeSyncs.Clear();

            running = true;

            // Abort all related threads
            if (recvThread != null)
            {
                recvThread.Abort();
                recvThread = null;
            }
            if (sendThread != null)
            {
                sendThread.Abort();
                sendThread = null;
            }
            if (parseThread != null)
            {
                parseThread.Abort();
                parseThread = null;
            }

            try
            {
                TLC = new ThreadLiveCheck();
                // Start receive thread
                recvThread = new Thread(new ThreadStart(recvLoop));
                recvThread.Name = "Network_Remote_Endpoint_Receive_Thread";
                recvThread.Start();

                // Start send thread
                sendThread = new Thread(new ThreadStart(sendLoop));
                sendThread.Name = "Network_Remote_Endpoint_Send_Thread";
                sendThread.Start();

                // Start parse thread
                parseThread = new Thread(new ThreadStart(parseLoop));
                parseThread.Name = "Network_Remote_Endpoint_Parse_Thread";
                parseThread.Start();
            }
            catch (Exception e)
            {
                Logging.error("Exception start remote endpoint: {0}", e.Message);
            }
        }

        // Aborts all related endpoint threads and data
        public void stop()
        {
            fullyStopped = true;

            Thread.Sleep(50); // clear any last messages

            state = RemoteEndpointState.Closed;
            running = false;

            Thread.Sleep(50); // wait for threads to stop

            lock (sendQueueMessagesHighPriority)
            {
                sendQueueMessagesHighPriority.Clear();
            }

            lock (sendQueueMessagesNormalPriority)
            {
                sendQueueMessagesNormalPriority.Clear();
            }

            lock (sendQueueMessagesLowPriority)
            {
                sendQueueMessagesLowPriority.Clear();
            }

            lock (recvRawQueueMessages)
            {
                recvRawQueueMessages.Clear();
            }

            lock (subscribedFilters)
            {
                subscribedFilters.Clear();
            }

            // Abort all related threads
            if (recvThread != null)
            {
                //recvThread.Abort();
                recvThread = null;
            }
            if (sendThread != null)
            {
                //sendThread.Abort();
                sendThread = null;
            }
            if (parseThread != null)
            {
                //parseThread.Abort();
                parseThread = null;
            }

            disconnect();
        }

        // Receive thread
        protected virtual void recvLoop()
        {
            Thread.CurrentThread.IsBackground = true;
            socketReadBuffer = new byte[8192];
            lastMessageStatTime = DateTime.UtcNow;
            while (running)
            {
                TLC.Report();
                // Let the protocol handler receive and handle messages
                try
                {
                    QueueMessageRaw? raw_msg = readSocketData();
                    if (raw_msg != null)
                    {
                        parseDataInternal((QueueMessageRaw)raw_msg);
                        messagesPerSecond++;
                    }
                }
                catch (Exception e)
                {
                    if(running)
                    {
                        Logging.warn(string.Format("recvRE: Disconnected client {0} with exception {1}", getFullAddress(), e.ToString()));
                    }
                    state = RemoteEndpointState.Closed;
                }

                // Check if the client disconnected
                if (state == RemoteEndpointState.Closed)
                {
                    running = false;
                    break;
                }

                TimeSpan timeSinceLastStat = DateTime.UtcNow - lastMessageStatTime;
                if (timeSinceLastStat.TotalSeconds < 0 || timeSinceLastStat.TotalSeconds > 2)
                {
                    lastMessageStatTime = DateTime.UtcNow;
                    lastMessagesPerSecond = (int)(messagesPerSecond / timeSinceLastStat.TotalSeconds);
                    messagesPerSecond = 0;
                }

                if (lastMessagesPerSecond < 1)
                {
                    lastMessagesPerSecond = 1;
                }
                    
                // Sleep a while to throttle the client
                // Check if there are too many messages
                // TODO TODO TODO this can be handled way better
                int total_message_count = NetworkQueue.getQueuedMessageCount() + NetworkQueue.getTxQueuedMessageCount();
                if (total_message_count > 10000)
                {
                    Thread.Sleep(1000);
                }
                else if (total_message_count > 5000)
                {
                    Thread.Sleep(500);
                }
                else
                {
                    // sleep for 1ms to throttle the client to 1000 messages/second
                    Thread.Sleep(1);
                }
            }
        }

        public virtual void disconnect()
        {
            // Close the client socket
            if (clientSocket != null)
            {
                try
                {
                    clientSocket.Shutdown(SocketShutdown.Both);
                    clientSocket.Close();
                    clientSocket = null;
                }
                catch (Exception e)
                {
                    Logging.warn(string.Format("recvRE: Could not shutdown client socket: {0}", e.ToString()));
                }
            }
        }

        protected void sendTimeSyncMessages()
        {
            // send 5 messages with current network timestamp
            List<byte> time_sync_data = new List<byte>();
            for (int i = 0; i < 5 && isConnected(); i++)
            {
                time_sync_data.Clear();
                time_sync_data.Add(2);
                time_sync_data.AddRange(BitConverter.GetBytes(Clock.getNetworkTimestampMillis()));
                try
                {
                    int time_sync_data_len = time_sync_data.ToArray().Length;
                    for (int sent = 0; sent < time_sync_data_len;)
                    {
                        sent += clientSocket.Send(time_sync_data.ToArray(), sent, time_sync_data_len, SocketFlags.None);
                    }
                }
                catch (Exception ex)
                {
                    // this may sometimes happen if clients/servers drop the connection from their side
                    Logging.warn(String.Format("Exception while attempting to send time sync: {0}.", ex.Message));
                }
            }
        }


        // Send thread
        protected void sendLoop()
        {
            // Prepare an special message object to use while sending, without locking up the queue messages
            QueueMessage active_message = new QueueMessage();

            if (enableSendTimeSyncMessages)
            {
                sendTimeSyncMessages();
            }

            int messageCount = 0;

            while (running)
            {
                TLC.Report();
                long curTime = Clock.getTimestamp();
                if(helloReceived == false && curTime - connectionStartTime > 10)
                {
                    // haven't received hello message for 10 seconds, stop running
                    Logging.warn("Node {0} hasn't received hello data from remote endpoint for over 10 seconds, disconnecting.", getFullAddress());
                    state = RemoteEndpointState.Closed;
                    running = false;
                    break;
                }
                if (curTime - lastDataReceivedTime > CoreConfig.pingTimeout)
                {
                    // haven't received any data for 10 seconds, stop running
                    Logging.warn("Node {0} hasn't received any data from remote endpoint for over {1} seconds, disconnecting.", getFullAddress(), CoreConfig.pingTimeout);
                    state = RemoteEndpointState.Closed;
                    running = false;
                    break;
                }
                if(curTime - lastDataSentTime > CoreConfig.pongInterval)
                {
                    try
                    {
                        clientSocket.Send(new byte[1] { 1 }, SocketFlags.None);
                        lastDataSentTime = curTime;
                        continue;
                    }
                    catch (Exception)
                    {
                        state = RemoteEndpointState.Closed;
                        running = false;
                        break;
                    }
                }

                bool message_found = false;
                lock (sendQueueMessagesHighPriority)
                {
                    lock (sendQueueMessagesNormalPriority)
                    {
                        if ((messageCount > 0 && messageCount % 5 == 0) || (sendQueueMessagesNormalPriority.Count == 0 && sendQueueMessagesHighPriority.Count == 0))
                        {
                            lock (sendQueueMessagesLowPriority)
                            {
                                if (sendQueueMessagesLowPriority.Count > 0)
                                {
                                    // Pick the oldest message
                                    active_message = sendQueueMessagesLowPriority[0];
                                    // Remove it from the queue
                                    sendQueueMessagesLowPriority.RemoveAt(0);
                                    message_found = true;
                                }
                            }
                            messageCount = 0;
                        }

                        if (message_found == false && ((messageCount > 0 && messageCount % 3 == 0) || sendQueueMessagesHighPriority.Count == 0))
                        {
                            if (sendQueueMessagesNormalPriority.Count > 0)
                            {
                                // Pick the oldest message
                                active_message = sendQueueMessagesNormalPriority[0];
                                // Remove it from the queue
                                sendQueueMessagesNormalPriority.RemoveAt(0);
                                message_found = true;
                            }
                        }

                        if (message_found == false && sendQueueMessagesHighPriority.Count > 0)
                        {
                            // Pick the oldest message
                            active_message = sendQueueMessagesHighPriority[0];
                            // Remove it from the queue
                            sendQueueMessagesHighPriority.RemoveAt(0);
                            message_found = true;
                        }
                    }
                }

                if (message_found)
                {
                    messageCount++;
                    // Active message set, attempt to send it
                    sendDataInternal(active_message.code, active_message.data, active_message.checksum);
                    if(active_message.code == ProtocolMessageCode.bye)
                    {
                        Thread.Sleep(500); // grace sleep to get the message through
                        running = false;
                        fullyStopped = true;
                    }
                }
                sendInventory();
                // sleep for 1ms to throttle sending to 1000 messages/second
                Thread.Sleep(1);
            }
        }

        public void addInventoryItem(InventoryItem item)
        {
            lock(inventory)
            {
                inventory.Add(item);
            }
        }

        protected void sendInventory()
        {
            IEnumerable<InventoryItem> items_to_send = null;
            lock (inventory)
            {
                if (inventory.Count() == 0)
                {
                    return;
                }
                if (inventoryLastSent > Clock.getTimestamp() - CoreConfig.inventoryInterval)
                {
                    return;
                }
                inventoryLastSent = Clock.getTimestamp();
                items_to_send = inventory.Take(CoreConfig.maxInventoryItems);
                inventory = inventory.Skip(CoreConfig.maxInventoryItems).ToList();
            }
            using (MemoryStream m = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(m))
                {
                    writer.WriteIxiVarInt(items_to_send.Count());
                    foreach (var item in items_to_send)
                    {
                        byte[] item_bytes = item.getBytes();
                        writer.WriteIxiVarInt(item_bytes.Length);
                        writer.Write(item_bytes);
                    }
                }
                sendDataInternal(ProtocolMessageCode.inventory, m.ToArray(), 0);
            }
        }

        // Parse thread
        protected void parseLoop()
        {
            // Prepare an special message object to use while sending, without locking up the queue messages
            QueueMessageRaw active_message = new QueueMessageRaw();

            while (running)
            {
                TLC.Report();
                try
                {
                    bool message_found = false;
                    lock (recvRawQueueMessages)
                    {
                        if (recvRawQueueMessages.Count > 0)
                        {
                            // Pick the oldest message
                            active_message = recvRawQueueMessages[0];
                            // Remove it from the queue
                            recvRawQueueMessages.RemoveAt(0);
                            message_found = true;
                        }
                    }

                    if (message_found)
                    {
                        // Active message set, add it to Network Queue
                        CoreProtocolMessage.readProtocolMessage(active_message, this);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }

                }
                catch (Exception e)
                {
                    Logging.error(String.Format("Exception occured for client {0} in parseLoopRE: {1} ", getFullAddress(), e));
                }
            }
        }

        protected void parseDataInternal(QueueMessageRaw message)
        {
            lock (recvRawQueueMessages)
            {
                recvRawQueueMessages.Add(message);
            }
        }


        // Internal function that sends data through the socket
        protected void sendDataInternal(ProtocolMessageCode code, byte[] data, uint checksum)
        {
            byte[] ba = prepareProtocolMessage(code, data, version, checksum);
            NetDump.Instance.appendSent(clientSocket, ba, ba.Length);
            try
            {
                for (int sentBytes = 0; sentBytes < ba.Length && running;)
                {
                    int bytesToSendCount = ba.Length - sentBytes;
                    if (bytesToSendCount > 8000)
                    {
                        bytesToSendCount = 8000;
                    }


                    int curSentBytes = clientSocket.Send(ba, sentBytes, bytesToSendCount, SocketFlags.None);

                    lastDataSentTime = Clock.getTimestamp();


                    // Sleep a bit to allow other threads to do their thing
                    if(curSentBytes < bytesToSendCount)
                    {
                        Thread.Sleep(1);
                    }

                    sentBytes += curSentBytes;
                    // TODO TODO TODO timeout
                }
                if (clientSocket.Connected == false)
                {
                    if (running)
                    {
                        Logging.warn(String.Format("sendRE: Failed senddata to remote endpoint {0}, Closing.", getFullAddress()));
                    }
                    state = RemoteEndpointState.Closed;
                }
            }
            catch (Exception e)
            {
                if (running)
                {
                    Logging.warn(String.Format("sendRE: Socket exception for {0}, closing. {1}", getFullAddress(), e));
                }
                state = RemoteEndpointState.Closed;

            }
        }

        private void addMessageToSendQueue(List<QueueMessage> message_queue, QueueMessage message)
        {
            if (message.helperData != null)
            {
                if (message_queue.Exists(x => x.code == message.code && x.helperData != null && message.helperData.SequenceEqual(x.helperData)))
                {
                    int msg_index = message_queue.FindIndex(x => x.code == message.code && x.helperData != null && message.helperData.SequenceEqual(x.helperData));
                    message_queue[msg_index] = message;
                    return;
                }
            }
            else
            {
                bool duplicate = message_queue.Exists(x => x.code == message.code && message.checksum == x.checksum);
                if (duplicate)
                {
                    Logging.warn(string.Format("Attempting to add a duplicate message (code: {0}) to the network queue for {1}", message.code, getFullAddress()));
                    return;
                }
            }
            // Check if there are too many messages
            if (message_queue.Count > CoreConfig.maxSendQueue)
            {
                message_queue.RemoveAt(10);
            }

            message_queue.Add(message);
        }


        // Sends data over the network
        public void sendData(ProtocolMessageCode code, byte[] data, byte[] helper_data = null)
        {
            if (data == null)
            {
                Logging.warn(string.Format("Invalid protocol message data for {0}", code));
                return;
            }

            QueueMessage message = getQueueMessage(code, data, helper_data);
            sendData(message);
        }

        public void sendData(QueueMessage message)
        {
            ProtocolMessageCode code = message.code;
            if (code == ProtocolMessageCode.bye || code == ProtocolMessageCode.keepAlivePresence
                || code == ProtocolMessageCode.getPresence || code == ProtocolMessageCode.updatePresence)
            {
                lock (sendQueueMessagesHighPriority)
                {
                    addMessageToSendQueue(sendQueueMessagesHighPriority, message);
                }
            }
            else if (code != ProtocolMessageCode.transactionData && code != ProtocolMessageCode.newTransaction)
            {
                lock (sendQueueMessagesNormalPriority)
                {
                    addMessageToSendQueue(sendQueueMessagesNormalPriority, message);
                }
            }
            else
            {
                lock (sendQueueMessagesLowPriority)
                {
                    addMessageToSendQueue(sendQueueMessagesLowPriority, message);
                }
            }
        }

        static public QueueMessage getQueueMessage(ProtocolMessageCode code, byte[] data, byte[] helper_data)
        {
            QueueMessage message = new QueueMessage();
            message.code = code;
            message.data = data;
            message.checksum = Crc32CAlgorithm.Compute(data);
            message.skipEndpoint = null;
            message.helperData = helper_data;

            return message;
        }

        public int getQueuedMessageCount()
        {
            lock (sendQueueMessagesHighPriority)
            {
                lock (sendQueueMessagesNormalPriority)
                {
                    lock (sendQueueMessagesLowPriority)
                    {
                        return sendQueueMessagesHighPriority.Count + sendQueueMessagesNormalPriority.Count + sendQueueMessagesLowPriority.Count;
                    }
                }
            }
        }

        public bool isConnected()
        {
            try
            {
                if (clientSocket == null)
                {
                    return false;
                }

                return clientSocket.Connected && running;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Get the ip/hostname and port
        public string getFullAddress(bool useIncomingPorts = false)
        {
            if(useIncomingPorts)
            {
                return address + ":" + incomingPort;
            }
            return fullAddress;
        }

        private MessageHeader parseHeader(byte[] header_bytes)
        {
            MessageHeader header = new MessageHeader();
            // we should have the full header, save the data length
            using (MemoryStream m = new MemoryStream(header_bytes))
            {
                using (BinaryReader reader = new BinaryReader(m))
                {
                    byte start = reader.ReadByte(); // skip start byte
                    if(start == 0xEA)
                    {
                        ProtocolMessageCode code = (ProtocolMessageCode)reader.ReadUInt16(); // skip message code
                        header.code = code;
                        uint data_length = reader.ReadUInt32(); // read data length
                        header.dataLen = data_length;
                        header.dataChecksum = reader.ReadUInt32(); // checksum crc32
                        
                        byte checksum = reader.ReadByte(); // header checksum byte

                        byte[] header_for_crc = new byte[11];
                        Array.Copy(header_bytes, header_for_crc, 11);

                        if (getHeaderChecksum(header_for_crc) != checksum)
                        {
                            Logging.warn("Header checksum mismatch");
                            return null;
                        }

                        if (data_length <= 0)
                        {
                            Logging.warn("Data length was {0}, code {1}", data_length, code);
                            return null;
                        }
                    }
                    else // 'X'
                    {
                        ProtocolMessageCode code = (ProtocolMessageCode)reader.ReadInt32(); // read message code
                        header.code = code;
                        int data_length = reader.ReadInt32(); // read data length
                        header.dataLen = (uint)data_length;
                        header.legacyDataChecksum = reader.ReadBytes(32); // read checksum sha512qu/sha512sq, 32 bytes

                        byte checksum = reader.ReadByte(); // header checksum byte
                        byte endByte = reader.ReadByte(); // end byte

                        if (endByte != 'I')
                        {
                            Logging.warn("Header end byte was not 'I'");
                            return null;
                        }

                        if (getHeaderChecksum(header_bytes.Take(41).ToArray()) != checksum)
                        {
                            Logging.warn("Header checksum mismatch");
                            return null;
                        }

                        if (data_length <= 0)
                        {
                            Logging.warn("Data length was {0}, code {1}", data_length, code);
                            return null;
                        }
                    }

                }
            }
            return header;
        }

        protected void readTimeSyncData()
        {
            if(timeSyncComplete)
            {
                return;
            }

            Socket socket = clientSocket;

            int rcv_count = 8;
            for (int i = 0; i < rcv_count && socket.Connected;)
            {
                i += socket.Receive(socketReadBuffer, i, rcv_count - i, SocketFlags.None);
                Thread.Sleep(1);
            }
            lock (timeSyncs)
            {
                long my_cur_time = Clock.getTimestampMillis();
                long cur_remote_time = BitConverter.ToInt64(socketReadBuffer, 0);
                long time_difference = my_cur_time - cur_remote_time;
                if (timeSyncs.Count > 0)
                {
                    TimeSyncData prev_tsd = timeSyncs.Last();
                    time_difference -= my_cur_time - prev_tsd.processedTime;
                }
                TimeSyncData tsd = new TimeSyncData() { timeDifference = time_difference, remoteTime = cur_remote_time, processedTime = my_cur_time };
                timeSyncs.Add(tsd);
                if(timeSyncs.Count() >= 5)
                {
                    timeSyncComplete = true;
                }
            }
        }

        // Reads data from a socket and returns a byte array
        protected QueueMessageRaw? readSocketData()
        {
            Socket socket = clientSocket;

            // Check for socket availability
            if (socket.Connected == false)
            {
                throw new Exception("Socket already disconnected at other end");
            }

            if (socket.Available < 1)
            {
                return null;
            }

            // Read multi-packet messages
            int old_header_len = 43; // old - start byte + message code (int32 4 bytes) + payload length (int32 4 bytes) + checksum (32 bytes) + header checksum (1 byte) + end byte = 43 bytes
            int new_header_len = 12; // new - start byte + message code (uint16 2 bytes) + payload length (uint32 4 bytes) + crc32 (uint32 4 bytes) + header checksum (1 byte) = 12 bytes
            byte[] header = new byte[old_header_len];
            int cur_header_len = 0;
            MessageHeader last_message_header = null;

            byte[] data = null;
            int cur_data_len = 0;

            try
            {
                
                int expected_data_len = 0;
                int expected_header_len = 0;
                int bytes_to_read = 1;
                while (socket.Connected && running)
                {
                    //int pos = bytesToRead > NetworkProtocol.recvByteHist.Length ? NetworkProtocol.recvByteHist.Length - 1 : bytesToRead;
                    /*lock (NetworkProtocol.recvByteHist)
                    {
                        NetworkProtocol.recvByteHist[pos]++;
                    }*/
                    int bytes_received = socket.Receive(socketReadBuffer, bytes_to_read, SocketFlags.None);
                    NetDump.Instance.appendReceived(socket, socketReadBuffer, bytes_received);
                    if (bytes_received <= 0)
                    {
                        // sleep a litte while waiting for bytes
                        Thread.Sleep(1);
                        // TODO should return null if a timeout occurs
                        continue;
                    }

                    lastDataReceivedTime = Clock.getTimestamp();
                    if (cur_header_len == 0)
                    {
                        if (socketReadBuffer[0] == 'X') // X is the message start byte
                        {
                            header[0] = socketReadBuffer[0];
                            cur_header_len = 1;
                            bytes_to_read = old_header_len - 1; // header length - start byte
                            expected_header_len = old_header_len;
                            version = 5;
                        }
                        else if (socketReadBuffer[0] == 0xEA) // 0xEA is the message start byte of v2 base protocol
                        {
                            header[0] = socketReadBuffer[0];
                            cur_header_len = 1;
                            bytes_to_read = new_header_len - 1; // header length - start byte
                            expected_header_len = new_header_len;
                            version = 6;
                        }
                        else if (helloReceived == false)
                        {
                            if (socketReadBuffer[0] == 2)
                            {
                                readTimeSyncData();
                            }
                        }
                        continue;
                    }

                    if(cur_header_len < expected_header_len)
                    {
                        Array.Copy(socketReadBuffer, 0, header, cur_header_len, bytes_received);
                        cur_header_len += bytes_received;
                        if (cur_header_len == expected_header_len)
                        {
                            last_message_header = parseHeader(header);
                            if(last_message_header != null)
                            {
                                cur_data_len = 0;
                                expected_data_len = (int)last_message_header.dataLen;
                                if(expected_data_len > CoreConfig.maxMessageSize)
                                {
                                    running = false;
                                    throw new Exception(string.Format("Message size ({0}B) received from the client is higher than the maximum message size allowed ({1}B) - protocol code: {2}.", expected_data_len, CoreConfig.maxMessageSize, last_message_header.code));
                                }
                                data = new byte[expected_data_len];
                                bytes_to_read = expected_data_len;
                                if (bytes_to_read > 8000)
                                {
                                    bytes_to_read = 8000;
                                }
                            }
                            else
                            {
                                cur_header_len = 0;
                                expected_data_len = 0;
                                data = null;
                                bytes_to_read = 1;
                                // Find next start byte if available
                                for (int i = cur_header_len - 1; i > 1; i--)
                                {
                                    if (header[i] == 'X')
                                    {
                                        cur_header_len = cur_header_len - i;
                                        Array.Copy(header, i, header, 0, cur_header_len);
                                        expected_header_len = old_header_len;
                                        bytes_to_read = expected_header_len - cur_header_len;
                                        version = 5;
                                        break;
                                    }
                                    else if (header[i] == 0xEA)
                                    {
                                        cur_header_len = cur_header_len - i;
                                        Array.Copy(header, i, header, 0, cur_header_len);
                                        expected_header_len = new_header_len;
                                        bytes_to_read = expected_header_len - cur_header_len;
                                        version = 6;
                                        break;
                                    }
                                }
                            }
                        }
                        else if (cur_header_len < expected_header_len)
                        {
                            bytes_to_read = expected_header_len - cur_header_len;
                        }
                    }else
                    {
                        Array.Copy(socketReadBuffer, 0, data, cur_data_len, bytes_received);
                        cur_data_len += bytes_received;
                        if (cur_data_len == expected_data_len)
                        {
                            QueueMessageRaw raw_message = new QueueMessageRaw() { 
                                checksum = last_message_header.dataChecksum,
                                code = last_message_header.code,
                                data = data,
                                legacyChecksum = last_message_header.legacyDataChecksum,
                                endpoint = this
                            };
                            return raw_message;
                        }
                        else if (cur_data_len > expected_data_len)
                        {
                            Logging.error("Unhandled edge case occured in RemoteEndPoint:readSocketData for node {0}", getFullAddress());
                            return null;
                        }
                        bytes_to_read = expected_data_len - cur_data_len;
                        if (bytes_to_read > 8000)
                        {
                            bytes_to_read = 8000;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (running)
                {
                    Logging.error("NET: endpoint {0} disconnected {1}", getFullAddress(), e);
                    throw;
                }
            }
            return null;
        }

        // Subscribe to event
        public bool attachEvent(NetworkEvents.Type type, byte[] filter)
        {
            if (address == null)
                return false;

            lock (subscribedFilters)
            {
                // Check the quota
                int num_subscribed_addresses = subscribedFilters.Values.Aggregate(0, (acc, f) => acc + f.numItems);
                if (num_subscribed_addresses > CoreConfig.maximumSubscribableEvents)
                {
                    return false;
                }
            }
            Cuckoo cuckoo_filter = null;
            try
            {
                cuckoo_filter = new Cuckoo(filter);
            } catch(Exception)
            {
                Logging.warn("Error while attempting to replace {0} filter for endpoint {1}",
                    type.ToString(),
                    getFullAddress()
                    );
                return false;
            }

            if (cuckoo_filter == null)
            {
                Logging.warn("Cannot attach event {0} to Remote Endpoint {1}, cuckoo filter is null.",
                    type.ToString(),
                    getFullAddress()
                    );
                return false;
            }


            lock (subscribedFilters) {
                // Subscribing a new cuckoo for a particular event type will replace the old one
                subscribedFilters.AddOrReplace(type, cuckoo_filter);
            }

            return true;
        }


        // Unsubscribe from event
        public bool detachEventType(NetworkEvents.Type type)
        {
            lock (subscribedFilters)
            {
                // Check if we're subscribed already to this address
                if (subscribedFilters.ContainsKey(type) == true)
                {
                    subscribedFilters.Remove(type);
                }
            }

            return true;
        }

        public bool detachEventAddress(NetworkEvents.Type type, byte[] address)
        {
            if(address == null)
            {
                return true;
            }
            lock(subscribedFilters)
            {
                if(subscribedFilters.ContainsKey(type) == true)
                {
                    subscribedFilters[type].Delete(address);
                }
            }
            return true;
        }

        // Check if the remote endpoint is subscribed to an event for a specific address
        // Returns true if subscribed
        public bool isSubscribedToAddress(NetworkEvents.Type type, byte[] address)
        {
            if (address == null)
                return false;

            lock (subscribedFilters)
            {
                if(subscribedFilters.ContainsKey(type) == true)
                {
                    return subscribedFilters[type].Contains(address);
                }
            }

            return false;
        }

        public long calculateTimeDifference()
        {
            lock (timeSyncs)
            {
                if(timeSyncs.Count == 0)
                {
                    return 0;
                }
                long time_diff = timeSyncs.OrderBy(x => x.timeDifference).First().timeDifference;
                return time_diff / 1000;
            }
        }

        /// <summary>
        ///  Prepares (serializes) a protocol message from the given Ixian message code and appropriate data. Checksum can be supplied, but 
        ///  if it isn't, this function will calculate it using the default method.
        /// </summary>
        /// <remarks>
        ///  This function can be used from the server and client side.
        ///  Please note: This function does not validate that the payload `data` conforms to the expected message for `code`. It is the 
        ///  caller's job to ensure that.
        /// </remarks>
        /// <param name="code">Message code.</param>
        /// <param name="data">Payload for the message.</param>
        /// <param name="checksum">Optional checksum. If not supplied, or if null, this function will calculate it with the default method.</param>
        /// <returns>Serialized message as a byte-field</returns>
        public static byte[] prepareProtocolMessage(ProtocolMessageCode code, byte[] data,int version, uint checksum)
        {
            byte[] result = null;

            // Prepare the protocol sections
            int data_length = data.Length;

            if (data_length > CoreConfig.maxMessageSize)
            {
                Logging.error("Tried to send data bigger than max allowed message size - {0} with code {1}.", data_length, code);
                return null;
            }

            using (MemoryStream m = new MemoryStream(4096))
            {
                using (BinaryWriter writer = new BinaryWriter(m))
                {
                    // Protocol sections are code, length, checksum, data
                    // Write each section in binary, in that specific order
                    bool add_end_byte = false;
                    if(version == 5)
                    {
                        writer.Write((byte)'X');
                        writer.Write((int)code);
                        writer.Write(data_length);
                        writer.Write(Crypto.sha512sqTrunc(data, 0, 0, 32));
                        add_end_byte = true;
                    }else
                    {
                        writer.Write((byte)0xEA);
                        writer.Write((ushort)code);
                        writer.Write((uint)data_length);
                        if(checksum == 0)
                        {
                            writer.Write(Crc32CAlgorithm.Compute(data));
                        }else
                        {
                            writer.Write(checksum);
                        }
                    }

                    writer.Flush();
                    m.Flush();

                    byte header_checksum = getHeaderChecksum(m.ToArray());
                    writer.Write(header_checksum);

                    if(add_end_byte)
                    {
                        writer.Write((byte)'I');
                    }
                    writer.Write(data);
#if TRACE_MEMSTREAM_SIZES
                    Logging.info(String.Format("CoreProtocolMessage::prepareProtocolMessage: {0}", m.Length));
#endif
                }
                result = m.ToArray();
            }

            return result;
        }

        /// <summary>
        ///  Calculates a single-byte checksum from the given header.
        /// </summary>
        /// <remarks>
        ///  A single byte of checksum is not extremely robust, but it is simple and fast.
        /// </remarks>
        /// <param name="header">Message header.</param>
        /// <returns>Checksum byte.</returns>
        private static byte getHeaderChecksum(byte[] header)
        {
            byte sum = 0x7F;
            for (int i = 0; i < header.Length; i++)
            {
                sum ^= header[i];
            }
            return sum;
        }
    }
}