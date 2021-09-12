﻿// Copyright (C) 2017-2020 Ixian OU
// This file is part of Ixian Core - www.github.com/ProjectIxian/Ixian-Core
//
// Ixian Core is free software: you can redistribute it and/or modify
// it under the terms of the MIT License as published
// by the Open Source Initiative.
//
// Ixian Core is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// MIT License for more details.

using IXICore.Meta;
using IXICore.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace IXICore
{
    public class AddressData
    {
        public byte[] nonce = null;
        public IxianKeyPair keyPair = null;
    }

    public class WalletStorage
    {
        public string filename { get; protected set; }

        public int walletVersion { get; protected set; } = 0;
        public bool viewingWallet { get; protected set; } = false;

        protected string walletPassword = ""; // TODO TODO TODO TODO wallet password, seed and keys should be encrypted in memory

        protected byte[] seedHash = null;
        protected byte[] masterSeed = null;
        protected byte[] derivedMasterSeed = null;

        protected readonly Dictionary<byte[], IxianKeyPair> myKeys = new Dictionary<byte[], IxianKeyPair>(new IXICore.Utils.ByteArrayComparer());
        protected readonly Dictionary<byte[], AddressData> myAddresses = new Dictionary<byte[], AddressData>(new IXICore.Utils.ByteArrayComparer());

        protected byte[] privateKey = null;
        protected byte[] publicKey = null;
        protected byte[] address = null;
        protected byte[] lastAddress = null;

        protected bool walletLoaded = false;

        protected byte[] baseNonce = null;

        public WalletStorage(string file_name)
        {
            filename = file_name;
        }

        public byte[] getPrimaryAddress()
        {
            return address;
        }

        public byte[] getPrimaryPrivateKey()
        {
            return privateKey;
        }

        public byte[] getPrimaryPublicKey()
        {
            return publicKey;
        }

        public byte[] getLastAddress()
        {
            // TODO TODO TODO TODO TODO improve if possible for v3 wallets
            // Also you have to take into account what happens when loading from file and the difference between v1 and v2 wallets (key related)
            lock (myAddresses)
            {
                return lastAddress;
            }
        }

        public byte[] getSeedHash()
        {
            return seedHash;
        }

        // Get the full wallet file path
        public string getFileName()
        {
            return filename;
        }

        public IxiNumber getMyTotalBalance(byte[] primary_address)
        {
            IxiNumber balance = 0;
            lock (myAddresses)
            {
                foreach (var entry in myAddresses)
                {
                    if (primary_address != null && !entry.Value.keyPair.addressBytes.SequenceEqual(primary_address))
                    {
                        continue;
                    }
                    IxiNumber amount = IxianHandler.getWalletBalance(entry.Key);
                    if (amount == 0)
                    {
                        continue;
                    }
                    balance += amount;
                }
            }
            return balance;
        }

        public Address generateNewAddress(Address key_primary_address, byte[] last_nonce, bool add_to_pool = true, bool write_to_file = true)
        {
            Address new_address = null;
            if(walletVersion < 2)
            {
                new_address = generateNewAddress_v0(key_primary_address, last_nonce, add_to_pool);
            }
            else
            {
                new_address = generateNewAddress_v1(key_primary_address, last_nonce, add_to_pool);
            }
            if(new_address != null)
            {
                if (write_to_file)
                {
                    writeWallet(walletPassword);
                }
            }
            return new_address;
        }

        public Address generateNewAddress_v0(Address key_primary_address, byte[] last_nonce, bool add_to_pool = true)
        {
            lock (myKeys)
            {
                if (!myKeys.ContainsKey(key_primary_address.address))
                {
                    return null;
                }

                IxianKeyPair kp = myKeys[key_primary_address.address];

                byte[] base_nonce = baseNonce;

                if (last_nonce == null)
                {
                    last_nonce = kp.lastNonceBytes;
                }

                List<byte> new_nonce = base_nonce.ToList();
                if (last_nonce != null)
                {
                    new_nonce.AddRange(last_nonce);
                }
                byte[] new_nonce_bytes = Crypto.sha512quTrunc(new_nonce.ToArray(), 0, 0, 16);

                Address new_address = new Address(key_primary_address.address, new_nonce_bytes);

                if (add_to_pool)
                {
                    kp.lastNonceBytes = new_nonce_bytes;
                    lock (myAddresses)
                    {
                        AddressData ad = new AddressData() { nonce = kp.lastNonceBytes, keyPair = kp };
                        myAddresses.Add(new_address.address, ad);
                        lastAddress = new_address.address;
                    }
                }

                return new_address;
            }
        }

        public Address generateNewAddress_v1(Address key_primary_address, byte[] last_nonce, bool add_to_pool = true)
        {
            lock (myKeys)
            {
                if (!myKeys.ContainsKey(key_primary_address.address))
                {
                    return null;
                }

                IxianKeyPair kp = myKeys[key_primary_address.address];

                byte[] base_nonce = baseNonce;

                if (last_nonce == null)
                {
                    last_nonce = kp.lastNonceBytes;
                }

                List<byte> new_nonce = base_nonce.ToList();
                if (last_nonce != null)
                {
                    new_nonce.AddRange(last_nonce);
                }
                byte[] new_nonce_bytes = Crypto.sha512sqTrunc(new_nonce.ToArray(), 0, 0, 16);

                Address new_address = new Address(key_primary_address.address, new_nonce_bytes);

                if (add_to_pool)
                {
                    kp.lastNonceBytes = new_nonce_bytes;
                    lock (myAddresses)
                    {
                        AddressData ad = new AddressData() { nonce = kp.lastNonceBytes, keyPair = kp };
                        myAddresses.Add(new_address.address, ad);
                        lastAddress = new_address.address;
                    }
                }

                return new_address;
            }
        }

        public IxianKeyPair generateNewKeyPair(bool writeToFile = true)
        {
            if (walletVersion != 3)
            {
                lock (myKeys)
                {
                    return myKeys.First().Value;
                }
            }

            IXICore.CryptoKey.KeyDerivation kd = new IXICore.CryptoKey.KeyDerivation(masterSeed);

            int key_count = 0;

            lock (myKeys)
            {
                key_count = myKeys.Count();
            }

            IxianKeyPair kp = kd.deriveKey(key_count, ConsensusConfig.defaultRsaKeySize, 65537);

            if (kp == null)
            {
                Logging.error("An error occured generating new key pair, unable to derive key.");
                return null;
            }

            if (!IXICore.CryptoManager.lib.testKeys(Encoding.Unicode.GetBytes("TEST TEST"), kp))
            {
                Logging.error("An error occured while testing the newly generated keypair, unable to produce a valid address.");
                return null;
            }
            Address addr = new Address(kp.publicKeyBytes);

            if (addr.address == null)
            {
                Logging.error("An error occured generating new key pair, unable to produce a valid address.");
                return null;
            }
            lock (myKeys)
            {
                lock (myAddresses)
                {
                    if (!writeToFile)
                    {
                        myKeys.Add(addr.address, kp);
                        AddressData ad = new AddressData() { nonce = kp.lastNonceBytes, keyPair = kp };
                        myAddresses.Add(addr.address, ad);
                    }
                    else
                    {
                        if (writeWallet(walletPassword))
                        {
                            myKeys.Add(addr.address, kp);
                            AddressData ad = new AddressData() { nonce = kp.lastNonceBytes, keyPair = kp };
                            myAddresses.Add(addr.address, ad);
                        }
                        else
                        {
                            Logging.error("An error occured while writing wallet file.");
                            return null;
                        }
                    }
                }
            }

            return kp;
        }

        public IxianKeyPair getKeyPair(byte[] address)
        {
            lock (myKeys)
            {
                if (myKeys.ContainsKey(address))
                {
                    return myKeys[address];
                }
                return null;
            }
        }

        public AddressData getAddress(byte[] address)
        {
            lock (myAddresses)
            {
                if (myAddresses.ContainsKey(address))
                {
                    return myAddresses[address];
                }
            }
            return null;
        }

        public bool isMyAddress(byte[] address)
        {
            lock (myAddresses)
            {
                if (myAddresses.ContainsKey(address))
                {
                    return true;
                }
            }
            return false;
        }

        public List<byte[]> extractMyAddressesFromAddressList(SortedDictionary<byte[], IxiNumber> address_list)
        {
            lock (myAddresses)
            {
                List<byte[]> found_address_list = new List<byte[]>();
                foreach (var entry in address_list)
                {
                    if (myAddresses.ContainsKey(entry.Key))
                    {
                        found_address_list.Add(entry.Key);
                    }
                }
                if(found_address_list.Count > 0)
                {
                    return found_address_list;
                }
            }
            return null;
        }

        public List<Address> getMyAddresses()
        {
            lock (myAddresses)
            {
                return myAddresses.Select(x => new Address(x.Key)).ToList();
            }
        }

        public List<string> getMyAddressesBase58()
        {
            lock (myAddresses)
            {
                return myAddresses.Select(x => (new Address(x.Key)).ToString()).ToList();
            }
        }

        public SortedDictionary<byte[], IxiNumber> generateFromListFromAddress(byte[] from_address, IxiNumber total_amount_with_fee, bool full_pubkey = false)
        {
            lock (myAddresses)
            {
                SortedDictionary<byte[], IxiNumber> tmp_from_list = new SortedDictionary<byte[], IxiNumber>(new ByteArrayComparer());
                if (full_pubkey)
                {
                    if (!myAddresses.ContainsKey(from_address))
                    {
                        return null;
                    }
                    AddressData ad = myAddresses[from_address];
                    tmp_from_list.Add(ad.nonce, total_amount_with_fee);
                }
                else
                {
                    tmp_from_list.Add(new byte[1] { 0 }, total_amount_with_fee);
                }
                return tmp_from_list;
            }
        }

        public SortedDictionary<byte[], IxiNumber> generateFromList(byte[] primary_address, IxiNumber total_amount_with_fee, List<byte[]> skip_addresses, List<Transaction> pending_transactions)
        {
            // TODO TODO TODO TODO  this won't work well once wallet v3 is activated
            lock (myAddresses)
            {
                Dictionary<byte[], IxiNumber> tmp_from_list = new Dictionary<byte[], IxiNumber>(new ByteArrayComparer());
                foreach (var entry in myAddresses)
                {
                    if(!entry.Value.keyPair.addressBytes.SequenceEqual(primary_address))
                    {
                        continue;
                    }

                    if (skip_addresses.Contains(entry.Key, new ByteArrayComparer()))
                    {
                        continue;
                    }

                    Wallet wallet = IxianHandler.getWallet(entry.Key);
                    if(wallet.type != WalletType.Normal)
                    {
                        continue;
                    }

                    IxiNumber amount = wallet.balance;
                    if(amount == 0)
                    {
                        continue;
                    }

                    tmp_from_list.Add(entry.Value.nonce, amount);
                }

                var tmp_from_list_ordered = tmp_from_list.OrderBy(x => x.Value.getAmount());

                SortedDictionary<byte[], IxiNumber> from_list = new SortedDictionary<byte[], IxiNumber>(new ByteArrayComparer());

                IxiNumber tmp_total_amount = 0;
                foreach (var entry in tmp_from_list_ordered)
                {
                    IxiNumber balance = entry.Value;
                    if (pending_transactions != null)
                    {
                        var tmp_pending_froms = pending_transactions.FindAll(x => x.fromList.ContainsKey(entry.Key));
                        foreach(var pending_from in tmp_pending_froms)
                        {
                            balance -= pending_from.fromList[entry.Key];
                        }
                    }

                    if(balance <= 0)
                    {
                        continue;
                    }

                    if (tmp_total_amount + balance >= total_amount_with_fee)
                    {
                        IxiNumber tmp_amount = total_amount_with_fee - tmp_total_amount;
                        from_list.Add(entry.Key, tmp_amount);
                        tmp_total_amount += tmp_amount;
                        break;
                    }
                    from_list.Add(entry.Key, balance);
                    tmp_total_amount += balance;
                }

                if (from_list.Count > 0 && tmp_total_amount == total_amount_with_fee)
                {
                    return from_list;
                }
                return null;
            }
        }

        public byte[] getNonceFromAddress(byte[] address)
        {
            foreach(var addr in myAddresses)
            {
                if(addr.Key.SequenceEqual(address))
                {
                    return addr.Value.nonce;
                }
            }
            return null;
        }



        protected bool readWallet_v1(BinaryReader reader, string password, bool verify_only = false)
        {
            if (walletVersion == 4)
            {
                char type = reader.ReadChar();
                if(type == 'v')
                {
                    viewingWallet = true;
                }
            }

            // Read the encrypted keys
            byte[] b_privateKey = null;
            byte[] b_baseNonce = null;
            if (viewingWallet)
            {
                int b_baseNonceLength = reader.ReadInt32();
                b_baseNonce = reader.ReadBytes(b_baseNonceLength);
            }
            else
            {
                int b_privateKeyLength = reader.ReadInt32();
                b_privateKey = reader.ReadBytes(b_privateKeyLength);
            }

            int b_publicKeyLength = reader.ReadInt32();
            byte[] b_publicKey = reader.ReadBytes(b_publicKeyLength);

            byte[] last_nonce_bytes = null;
            byte[] b_last_nonce = null;
            if (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                int b_last_nonceLength = reader.ReadInt32();
                b_last_nonce = reader.ReadBytes(b_last_nonceLength);
            }


            try
            {
                // Decrypt
                // Suppress decryption errors in console output
                bool console_output = Logging.consoleOutput;
                Logging.consoleOutput = false;
                byte[] private_key = null;
                byte[] base_nonce = null;
                if (viewingWallet)
                {
                    base_nonce = CryptoManager.lib.decryptWithPassword(b_baseNonce, password, false);
                    Logging.flush();
                    Logging.consoleOutput = console_output;
                    if (base_nonce == null)
                    {
                        Logging.error("Unable to decrypt wallet, an incorrect password was used.");
                        return false;
                    }
                }else
                {
                    private_key = CryptoManager.lib.decryptWithPassword(b_privateKey, password, false);
                    Logging.flush();
                    Logging.consoleOutput = console_output;
                    if (private_key == null)
                    {
                        Logging.error("Unable to decrypt wallet, an incorrect password was used.");
                        return false;
                    }
                }
                byte[] public_key = CryptoManager.lib.decryptWithPassword(b_publicKey, password, false);
                if (public_key == null)
                {
                    Logging.error("Unable to decrypt wallet, file is probably corrupted.");
                    return false;
                }
                if (b_last_nonce != null)
                {
                    last_nonce_bytes = CryptoManager.lib.decryptWithPassword(b_last_nonce, password, false);
                    if (last_nonce_bytes == null)
                    {
                        Logging.error("Unable to decrypt wallet, file is probably corrupted.");
                        return false;
                    }
                }
                if (verify_only)
                {
                    return true;
                }
                privateKey = private_key;
                baseNonce = base_nonce;
                publicKey = public_key;
                if (baseNonce == null)
                {
                    if(walletVersion < 2)
                    {
                        baseNonce = Crypto.sha512quTrunc(privateKey, publicKey.Length, 64);
                    }
                    else
                    {
                        baseNonce = Crypto.sha512sqTrunc(privateKey, publicKey.Length, 64);
                    }
                }
                walletPassword = password;
            }
            catch (Exception)
            {
                Logging.error(string.Format("Unable to decrypt wallet, an incorrect password was used."));
                return false;
            }

            Address addr = new Address(publicKey);
            lastAddress = address = addr.address;

            masterSeed = address;
            seedHash = address;
            derivedMasterSeed = masterSeed;

            IxianKeyPair kp = new IxianKeyPair();
            kp.privateKeyBytes = privateKey;
            kp.publicKeyBytes = publicKey;
            kp.addressBytes = address;
            lock (myKeys)
            {
                myKeys.Add(address, kp);
            }
            lock (myAddresses)
            {
                AddressData ad = new AddressData() { nonce = new byte[1] { 0 }, keyPair = kp };
                myAddresses.Add(address, ad);

                if (last_nonce_bytes != null)
                {
                    bool last_address_found = false;
                    while (last_address_found == false)
                    {
                        if (kp.lastNonceBytes != null && last_nonce_bytes.SequenceEqual(kp.lastNonceBytes))
                        {
                            last_address_found = true;
                        }
                        else
                        {
                            generateNewAddress(addr, null, true, false);
                        }
                    }
                }
            }
            return true;
        }

        protected bool readWallet_v3(BinaryReader reader, string password, bool verify_only = false)
        {
            // Read the master seed
            int b_master_seed_length = reader.ReadInt32();
            byte[] b_master_seed = reader.ReadBytes(b_master_seed_length);

            try
            {
                // Decrypt
                // Suppress decryption errors in console output
                bool console_output = Logging.consoleOutput;
                Logging.consoleOutput = false;
                byte[] master_seed = CryptoManager.lib.decryptWithPassword(b_master_seed, password, true);
                Logging.flush();
                Logging.consoleOutput = console_output;
                if (master_seed == null)
                {
                    Logging.error(string.Format("Unable to decrypt wallet, an incorrect password was used."));
                    return false;
                }
                byte[] seed_hash = Crypto.sha512sqTrunc(masterSeed);
                if (!verify_only)
                {
                    masterSeed = master_seed;
                    seedHash = seed_hash;
                    walletPassword = password;
                }
            }
            catch (Exception)
            {
                Logging.error(string.Format("Unable to decrypt wallet, an incorrect password was used."));
                return false;
            }

            int key_count = reader.ReadInt32();
            for (int i = 0; i < key_count; i++)
            {
                int len = reader.ReadInt32();
                if (reader.BaseStream.Position + len > reader.BaseStream.Length)
                {
                    Logging.error("Wallet file is corrupt, expected more data than available.");
                    break;
                }
                byte[] enc_private_key = reader.ReadBytes(len);

                len = reader.ReadInt32();
                if (reader.BaseStream.Position + len > reader.BaseStream.Length)
                {
                    Logging.error("Wallet file is corrupt, expected more data than available.");
                    break;
                }
                byte[] enc_public_key = reader.ReadBytes(len);

                len = reader.ReadInt32();
                if (reader.BaseStream.Position + len > reader.BaseStream.Length)
                {
                    Logging.error("Wallet file is corrupt, expected more data than available.");
                    break;
                }
                byte[] enc_nonce = null;
                if (len > 0)
                {
                    enc_nonce = reader.ReadBytes(len);
                }

                byte[] dec_private_key = CryptoManager.lib.decryptWithPassword(enc_private_key, password, true);
                if (dec_private_key == null)
                {
                    return false;
                }
                byte[] dec_public_key = CryptoManager.lib.decryptWithPassword(enc_public_key, password, true);
                if (dec_public_key == null)
                {
                    return false;
                }
                byte[] tmp_address = (new Address(dec_public_key)).address;

                IxianKeyPair kp = new IxianKeyPair();
                kp.privateKeyBytes = dec_private_key;
                kp.publicKeyBytes = dec_public_key;
                kp.addressBytes = tmp_address;
                if (enc_nonce != null)
                {
                    kp.lastNonceBytes = CryptoManager.lib.decryptWithPassword(enc_nonce, password, true);
                    if (kp.lastNonceBytes == null)
                    {
                        return false;
                    }
                }

                if (verify_only)
                {
                    continue;
                }

                if (privateKey == null)
                {
                    privateKey = dec_private_key;
                    publicKey = dec_public_key;
                    lastAddress = address = tmp_address;
                    // TODO baseNonce should be used for each separate key
                    baseNonce = Crypto.sha512sqTrunc(privateKey, publicKey.Length, 64);
                }

                lock (myKeys)
                {
                    myKeys.Add(tmp_address, kp);
                }
                lock (myAddresses)
                {
                    AddressData ad = new AddressData() { nonce = new byte[1] { 0 }, keyPair = kp };
                    myAddresses.Add(tmp_address, ad);
                }
            }

            int seed_len = reader.ReadInt32();
            byte[] enc_derived_seed = reader.ReadBytes(seed_len);
            byte[] derived_master_seed = CryptoManager.lib.decryptWithPassword(enc_derived_seed, password, true);
            if (derived_master_seed == null)
            {
                return false;
            }
            if (!verify_only)
            {
                derivedMasterSeed = derived_master_seed;
            }
            return true;
        }

        public bool walletExists()
        {
            if (File.Exists(filename))
            {
                return true;
            }
            return false;
        }

        public void convertWalletFromIxiHex(string file_name)
        {
            string wallet_string = File.ReadAllText(file_name);
            
            if (wallet_string.Take(6).SequenceEqual("IXIHEX"))
            {
                Logging.info("Converting wallet from IXIHEX to binary");
                File.WriteAllBytes(file_name, Crypto.stringToHash((new string(wallet_string.Skip(6).ToArray())).Trim()));
            }
        }

        public bool verifyWallet(string file_name, string password)
        {
            if (File.Exists(file_name) == false)
            {
                Logging.log(LogSeverity.error, "Cannot read wallet file.");
                return false;
            }

            convertWalletFromIxiHex(file_name);

            BinaryReader reader;
            try
            {
                reader = new BinaryReader(new FileStream(file_name, FileMode.Open));
            }
            catch (Exception e)
            {
                Logging.log(LogSeverity.error, String.Format("Cannot open wallet file. {0}", e.Message));
                return false;
            }
            bool success = false;
            try
            {
                // Read the wallet version
                int wallet_version = reader.ReadInt32();
                if (wallet_version == 1 || wallet_version == 2 || wallet_version == 4)
                {
                    success = readWallet_v1(reader, password, true);
                }
                else if (wallet_version == 3)
                {
                    success = readWallet_v3(reader, password, true);
                }
                else
                {
                    Logging.error("Unknown wallet version {0}", wallet_version);
                    wallet_version = 0;
                    success = false;
                }
            }
            catch (Exception e)
            {
                Logging.error("Cannot read from wallet file. {0}", e.Message);
                success = false;
            }
            reader.Close();

            return success;

        }

        // Try to read wallet information from the file
        public bool readWallet(string password)
        {
            if (walletLoaded)
            {
                Logging.error("Can't read wallet, wallet already loaded.");
                return false;
            }

            if (File.Exists(filename) == false)
            {
                Logging.error("Cannot read wallet file.");
                return false;
            }

            convertWalletFromIxiHex(filename);

            Logging.info("Wallet file found, reading data...");
            Logging.flush();

            BinaryReader reader;
            try
            {
                reader = new BinaryReader(new FileStream(filename, FileMode.Open));
            }
            catch (Exception e)
            {
                Logging.error("Cannot open wallet file. {0}", e.Message);
                return false;
            }
            bool success = false;
            try
            {
                // Read the wallet version
                walletVersion = reader.ReadInt32();
                if (walletVersion == 1 || walletVersion == 2 || walletVersion == 4)
                {
                    success = readWallet_v1(reader, password);
                }
                else if (walletVersion == 3)
                {
                    success = readWallet_v3(reader, password);
                }
                else
                {
                    Logging.error("Unknown wallet version {0}", walletVersion);
                    walletVersion = 0;
                    success = false;
                }
            }
            catch (Exception e)
            {
                Logging.error("Cannot read from wallet file. {0}", e.Message);
                success = false;
            }
            reader.Close();
            if (success)
            {
                walletLoaded = true;
                if (myAddresses.Count > 0)
                {
                    backup();
                }
            }

            return success;
        }

        public bool backup()
        {
            string wallet_id = new String(getMyAddressesBase58().First().Take(8).ToArray());
            string expected_backup_file = filename + "." + wallet_id + ".bak";
            // create a backup of the new wallet file
            if (!File.Exists(expected_backup_file))
            {
                File.Copy(filename, expected_backup_file);
                return true;
            }
            return false;
        }

        public bool writeWallet(string password)
        {
            walletPassword = password;
            if (walletVersion == 1 || walletVersion == 2 || walletVersion == 4)
            {
                return writeWallet_v1(walletPassword);
            }
            if (walletVersion == 3)
            {
                return writeWallet_v3(walletPassword);
            }
            return false;
        }

        // Write the wallet to the file
        protected bool writeWallet_v1(string password)
        {
            if (password.Length < 10)
                return false;

            // Encrypt data first
            byte[] b_privateKey = CryptoManager.lib.encryptWithPassword(privateKey, password, false);
            byte[] b_baseNonce = CryptoManager.lib.encryptWithPassword(baseNonce, password, false);
            byte[] b_publicKey = CryptoManager.lib.encryptWithPassword(publicKey, password, false);

            BinaryWriter writer;
            try
            {
                writer = new BinaryWriter(new FileStream(filename, FileMode.Create));
            }
            catch (Exception e)
            {
                Logging.error("Cannot create wallet file. {0}", e.Message);
                return false;
            }

            try
            {
                // TODO Omega - automatically upgrade to wallet v4
                writer.Write(walletVersion);
                if(walletVersion == 4)
                {
                    if (viewingWallet)
                    {
                        writer.Write('v');
                        // Write the address keypair
                        writer.Write(b_baseNonce.Length);
                        writer.Write(b_baseNonce);
                    }
                    else
                    {
                        writer.Write('f');
                        // Write the address keypair
                        writer.Write(b_privateKey.Length);
                        writer.Write(b_privateKey);
                    }
                }else
                {
                    writer.Write(b_privateKey.Length);
                    writer.Write(b_privateKey);
                }


                writer.Write(b_publicKey.Length);
                writer.Write(b_publicKey);

                if (myKeys.First().Value.lastNonceBytes != null)
                {
                    byte[] b_last_nonce = CryptoManager.lib.encryptWithPassword(myKeys.First().Value.lastNonceBytes, password, false);
                    writer.Write(b_last_nonce.Length);
                    writer.Write(b_last_nonce);
                }

            }

            catch (Exception e)
            {
                Logging.error("Cannot write to wallet file. {0}", e.Message);
                return false;
            }

            writer.Close();

            return true;
        }

        // Write the wallet to the file
        protected bool writeWallet_v3(string password)
        {
            if (password.Length < 10)
                return false;

            BinaryWriter writer;
            try
            {
                writer = new BinaryWriter(new FileStream(filename, FileMode.Create));
            }
            catch (Exception e)
            {
                Logging.error("Cannot create wallet file. {0}", e.Message);
                return false;
            }

            try
            {
                writer.Write(walletVersion);

                // Write the master seed
                byte[] enc_master_seed = CryptoManager.lib.encryptWithPassword(masterSeed, password, true);
                writer.Write(enc_master_seed.Length);
                writer.Write(enc_master_seed);

                lock (myKeys)
                {
                    writer.Write(myKeys.Count());

                    foreach (var entry in myKeys)
                    {
                        byte[] enc_private_key = CryptoManager.lib.encryptWithPassword(entry.Value.privateKeyBytes, password, true);
                        writer.Write(enc_private_key.Length);
                        writer.Write(enc_private_key);

                        byte[] enc_public_key = CryptoManager.lib.encryptWithPassword(entry.Value.publicKeyBytes, password, true);
                        writer.Write(enc_public_key.Length);
                        writer.Write(enc_public_key);

                        if (entry.Value.lastNonceBytes != null)
                        {
                            byte[] enc_nonce = CryptoManager.lib.encryptWithPassword(entry.Value.lastNonceBytes, password, true);
                            writer.Write(enc_nonce.Length);
                            writer.Write(enc_nonce);
                        }
                        else
                        {
                            writer.Write((int)0);
                        }
                    }
                }

                byte[] enc_derived_master_seed = CryptoManager.lib.encryptWithPassword(derivedMasterSeed, password, true);
                writer.Write(enc_derived_master_seed.Length);
                writer.Write(enc_derived_master_seed);
            }

            catch (Exception e)
            {
                Logging.error("Cannot write to wallet file. {0}", e.Message);
                return false;
            }

            writer.Close();

            return true;
        }

        // Deletes the wallet file if it exists
        public bool deleteWallet()
        {
            if (File.Exists(filename) == false)
            {
                return false;
            }

            File.Delete(filename);
            return true;
        }

        // Generate a new wallet with matching private/public key pairs
        public bool generateWallet(string password)
        {
            if(walletLoaded)
            {
                Logging.error("Can't generate wallet, wallet already loaded.");
                return false;
            }

            Logging.flush();

            walletVersion = 2;
            walletPassword = password;

            Logging.log(LogSeverity.info, "Generating primary wallet keys, this may take a while, please wait...");

            //IxianKeyPair kp = generateNewKeyPair(false);
            IxianKeyPair kp = CryptoManager.lib.generateKeys(ConsensusConfig.defaultRsaKeySize);

            if (kp == null)
            {
                Logging.error("Error creating wallet, unable to generate a new keypair.");
                return false;
            }

            privateKey = kp.privateKeyBytes;
            publicKey = kp.publicKeyBytes;
            baseNonce = Crypto.sha512sqTrunc(privateKey, publicKey.Length, 64);

            Address addr = new Address(publicKey);
            lastAddress = address = addr.address;

            masterSeed = address;
            seedHash = address;
            derivedMasterSeed = masterSeed;

            kp.addressBytes = address;

            myKeys.Add(address, kp);
            myAddresses.Add(address, new AddressData() { keyPair = kp, nonce = new byte[1] { 0 } });


            Logging.info(String.Format("Public Key: {0}", Crypto.hashToString(publicKey)));
            Logging.info(String.Format("Public Node Address: {0}", Base58Check.Base58CheckEncoding.EncodePlain(address)));

            // Wait for any pending log messages to be written
            Logging.flush();

            Console.WriteLine();
            Console.Write("Your IXIAN address is ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(Base58Check.Base58CheckEncoding.EncodePlain(address));
            Console.ResetColor();
            Console.WriteLine();

            // Write the new wallet data to the file
            if (writeWallet(password))
            {
                backup();
                return true;
            }

            walletLoaded = true;

            return false;
        }

        public void scanForLostAddresses()
        {
            bool new_address_found = false;
            foreach(var key in myKeys)
            {
                Address primary_address = new Address(key.Value.addressBytes);
                byte[] last_nonce = key.Value.lastNonceBytes;
                for (int i = 0; i < 100; i++)
                {
                    Address new_address = generateNewAddress(primary_address, last_nonce, false, false);
                    if(IxianHandler.getWalletBalance(new_address.address) > 0)
                    {
                        new_address_found = true;
                        for(int j = 0; j <= i; j++)
                        {
                            generateNewAddress(primary_address, null, true, false);
                        }
                        i = 0;
                    }
                    last_nonce = new_address.nonce;
                }
            }
            if (new_address_found)
            {
                writeWallet(walletPassword);
            }
        }

        public byte[] getRawWallet()
        {
            return File.ReadAllBytes(filename);
        }

        public byte[] getRawViewingWallet()
        {
            string password = walletPassword;
            if (password.Length < 10)
                return null;

            // Encrypt data first
            byte[] b_baseNonce = CryptoManager.lib.encryptWithPassword(baseNonce, password, false);
            byte[] b_publicKey = CryptoManager.lib.encryptWithPassword(publicKey, password, false);

            using (MemoryStream m = new MemoryStream())
            {
                using (BinaryWriter w = new BinaryWriter(m))
                {
                    try
                    {
                        w.Write(4);
                        w.Write('v');

                        // Write the address keypair
                        w.Write(b_baseNonce.Length);
                        w.Write(b_baseNonce);

                        w.Write(b_publicKey.Length);
                        w.Write(b_publicKey);

                        if (myKeys.First().Value.lastNonceBytes != null)
                        {
                            byte[] b_last_nonce = CryptoManager.lib.encryptWithPassword(myKeys.First().Value.lastNonceBytes, password, false);
                            w.Write(b_last_nonce.Length);
                            w.Write(b_last_nonce);
                        }

                    }
                    catch (Exception e)
                    {
                        Logging.error("Cannot write to wallet file. {0}", e.Message);
                        return null;
                    }
                }
                return m.ToArray();
            }
        }

        public bool isLoaded()
        {
            return walletLoaded;
        }

        public bool isValidPassword(string password)
        {
            if(password != null && password.Length > 0 && password == walletPassword)
            {
                return true;
            }
            return false;
        }
    }
}
