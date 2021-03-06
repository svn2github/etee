﻿/*
 * This file is part of .Net ETEE for eHealth.
 * Copyright (C) 2014 Egelke
 * 
 * .Net ETEE for eHealth is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * .Net ETEE for eHealth  is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.

 * You should have received a copy of the GNU Lesser General Public License
 * along with .Net ETEE for eHealth.  If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509.Store;
using Egelke.EHealth.Etee.Crypto.Configuration;
using Egelke.EHealth.Etee.Crypto.Utils;
using BC = Org.BouncyCastle;
using System.Security.Permissions;
using System;
using System.Threading;
using System.Diagnostics;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using System.Collections;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.X509.Extension;
using Org.BouncyCastle.X509;
using System.Net;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Asn1.Esf;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Asn1.Cms;
using Egelke.EHealth.Client.Pki;
using Org.BouncyCastle.Utilities.IO;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Tsp;
using Egelke.EHealth.Etee.Crypto.Store;
using Egelke.EHealth.Etee.Crypto.Sender;
using System.Linq;

namespace Egelke.EHealth.Etee.Crypto
{
    internal class TripleWrapper : IDataSealer, IDataCompleter, ITmaDataCompleter
    {

        private TraceSource trace = new TraceSource("Egelke.EHealth.Etee");

        private Level level;

        //The sender authentication certificate
        private X509Certificate2 authentication;

        //The sender signature certificate
        private X509Certificate2 signature;

        private ITimestampProvider timestampProvider;

        private X509Certificate2Collection extraStore;

        internal TripleWrapper(Level level, X509Certificate2 authentication, X509Certificate2 signature, ITimestampProvider timestampProvider, X509Certificate2Collection extraStore)
        {
            //basic checks
            if (level == Level.L_Level || level == Level.A_level) throw new ArgumentException("level", "Only levels B, T, LT and LTA are allowed");

            this.level = level;
            this.signature = signature;
            this.authentication = authentication;
            this.timestampProvider = timestampProvider;
            this.extraStore = extraStore;
        }

        #region DataCompleter Members

        public Stream Complete(Stream sealedData)
        {
            TimemarkKey timemarkKey;
            return Complete(sealedData, out timemarkKey);
        }

        public Stream Complete(Stream sealedData, out TimemarkKey timemarkKey)
        {
            trace.TraceEvent(TraceEventType.Information, 0, "Completing the provided sealed message with revocation and time info according to the level {0}", this.level);

            ITempStreamFactory factory = NewFactory(sealedData);
            Stream completed = factory.CreateNew();
            Complete(this.level, completed, sealedData, null, out timemarkKey);
            completed.Position = 0;

            return completed;
        }

        #endregion

        #region DataSealer Members

        public Stream Seal(Stream unsealed, params EncryptionToken[] tokens)
        {
            return Seal(unsealed, null, tokens);
        }

        public Stream Seal(Stream unsealed, params X509Certificate2[] certs)
        {
            ITempStreamFactory factory = NewFactory(unsealed);
            return Seal(factory, unsealed, null, certs);
        }

        public Stream Seal(Stream unsealed, SecretKey key, params EncryptionToken[] tokens)
        {
            ITempStreamFactory factory = NewFactory(unsealed);
            return Seal(factory, unsealed, key, ConverToX509Certificates(tokens));
        }

        #endregion

        private ITempStreamFactory NewFactory(Stream stream)
        {
            return stream.Length > Settings.Default.InMemorySize ? (ITempStreamFactory)new TempFileStreamFactory() : (ITempStreamFactory)new MemoryStreamFactory();
        }

        private X509Certificate2[] ConverToX509Certificates(EncryptionToken[] tokens)
        {
            X509Certificate2[] certs = new X509Certificate2[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                certs[i] = tokens[i].ToCertificate();
            }
            return certs;
        }

        private Stream Seal(ITempStreamFactory factory, Stream unsealedStream, SecretKey key, X509Certificate2[] certs)
        {
            trace.TraceEvent(TraceEventType.Information, 0, "Sealing message of {0} bytes for {1} known recipients and {2} unknown recipients to level {3}",
                unsealedStream.Length, certs.Length, key == null ? 0 : 1, this.level);

            //Create inner signed stream
            Stream signed = factory.CreateNew();
            using (signed)
            {
                //Inner sign
                Stream intermedate = factory.CreateNew();
                using (intermedate)
                {
                    if (signature == null)
                    {
                        Sign(signed, unsealedStream, authentication);

                        signed.Position = 0;

                        signed.CopyTo(intermedate);
                    }
                    else
                    {
                        Sign(signed, unsealedStream, signature);

                        signed.Position = 0;

                        //Add the certificate and revocation info only (no time-stamp)
                        TimemarkKey timemarkKey;
                        Complete(this.level & ~Level.T_Level, intermedate, signed, signature, out timemarkKey);
                    }

                    intermedate.Position = 0;

                    //Create  encrypted stream
                    Stream signedEncrypted = factory.CreateNew();
                    using (signedEncrypted)
                    {
                        //Encrypt
                        Encrypt(signedEncrypted, intermedate, certs, key);

                        //Outer sign with retry for eID
                        int tries = 0;
                        bool success = false;
                        Stream sealedStream = null;
                        while (!success)
                        {
                            signedEncrypted.Position = 0;

                            try
                            {
                                //This is the output, so we need to make it a temp stream (temp file or memory stream)
                                sealedStream = factory.CreateNew();
                                Sign(sealedStream, signedEncrypted, authentication);
                                success = true;
                            }
                            catch (CryptographicException ce)
                            {
                                //Keep track
                                trace.TraceEvent(TraceEventType.Warning, 0, "Failed to put outer signature (try {0}): {1}", tries, ce);
                                if (tries++ < 4)
                                {
                                    sealedStream.Close();
                                    sealedStream = null;
                                    Thread.Sleep((int)Math.Pow(10, tries)); //wait longer and longer
                                }
                                else
                                {
                                    sealedStream.Close();
                                    throw ce;
                                }
                            }
                        }

                        try
                        {
                            sealedStream.Position = 0; //reset the stream

                            //Complete the outer signature with revocation info & time-stamp if needed
                            TimemarkKey timemarkKey;
                            Stream completedStream = factory.CreateNew();
                            Complete(this.level, completedStream, sealedStream, authentication, out timemarkKey);

                            completedStream.Position = 0; //reset the stream

                            return completedStream;
                        }
                        finally
                        {
                            sealedStream.Close();
                        }
                    }
                }
            }
        }

        protected void Sign(Stream signed, Stream unsigned, X509Certificate2 selectedCert)
        {
            BC::X509.X509Certificate bcSelectedCert = DotNetUtilities.FromX509Certificate(selectedCert);
            trace.TraceEvent(TraceEventType.Information, 0, "Signing the message in name of {0}", selectedCert.Subject);

            //Signing time
            DateTime signingTime = DateTime.UtcNow;

            CmsSignedDataStreamGenerator signedGenerator = new CmsSignedDataStreamGenerator();

            //For compatibility we don't add it to the CMS (most implementations, including BC, don't support OCSP here)
            //IX509Store crlStore = X509StoreFactory.Create("CRL/COLLECTION", new X509CollectionStoreParameters(crl's));
            //signedGenerator.AddCrls(crlStore);

            //add signed attributes to the signature (own signing time)
            IDictionary signedAttrDictionary = new Hashtable();
            BC::Asn1.Cms.Attribute signTimeattr = new BC::Asn1.Cms.Attribute(CmsAttributes.SigningTime,
                    new DerSet(new BC::Asn1.Cms.Time(signingTime)));
            signedAttrDictionary.Add(signTimeattr.AttrType, signTimeattr);
            BC::Asn1.Cms.AttributeTable signedAttrTable = new BC.Asn1.Cms.AttributeTable(signedAttrDictionary);

            //Add the signatures
            SignatureAlgorithm signAlgo;
            if (((RSACryptoServiceProvider)selectedCert.PrivateKey).CspKeyContainerInfo.Exportable) {
                signAlgo =  EteeActiveConfig.Seal.NativeSignatureAlgorithm;
                signedGenerator.AddSigner(DotNetUtilities.GetKeyPair(selectedCert.PrivateKey).Private,
                    bcSelectedCert, signAlgo.EncryptionAlgorithm.Value, signAlgo.DigestAlgorithm.Value,
                    signedAttrTable, null);
            } else {
                signAlgo = EteeActiveConfig.Seal.WindowsSignatureAlgorithm;
                signedGenerator.AddSigner(new ProxyRsaKeyParameters((RSACryptoServiceProvider)selectedCert.PrivateKey),
                    bcSelectedCert, signAlgo.EncryptionAlgorithm.Value, signAlgo.DigestAlgorithm.Value,
                    signedAttrTable, null);
            }
            trace.TraceEvent(TraceEventType.Verbose, 0, "Added Signer [EncAlgo={0} ({1}), DigestAlgo={2} ({3})",
                signAlgo.EncryptionAlgorithm.FriendlyName,
                signAlgo.EncryptionAlgorithm.Value,
                signAlgo.DigestAlgorithm.FriendlyName,
                signAlgo.DigestAlgorithm.Value);

            Stream signingStream = signedGenerator.Open(signed, true);
            trace.TraceEvent(TraceEventType.Verbose, 0, "Create embedded signed message (still empty)");
            try
            {
                unsigned.CopyTo(signingStream);
                trace.TraceEvent(TraceEventType.Verbose, 0, "Message copied and digest calculated");
            }
            finally
            {
                signingStream.Close();
                trace.TraceEvent(TraceEventType.Verbose, 0, "Signature block added");
            }
        }

        protected void Encrypt(Stream cipher, Stream clear, ICollection<X509Certificate2> certs, SecretKey key)
        {
            trace.TraceEvent(TraceEventType.Information, 0, "Encrypting message for {0} known and {1} unknown recipient",
                certs == null ? 0 : certs.Count, key == null ? 0 : 1);
            CmsEnvelopedDataStreamGenerator encryptGenerator = new CmsEnvelopedDataStreamGenerator();
            if (certs != null)
            {
                foreach (X509Certificate2 cert in certs)
                {
                    BC::X509.X509Certificate bcCert = DotNetUtilities.FromX509Certificate(cert);
                    encryptGenerator.AddKeyTransRecipient(bcCert);
                    trace.TraceEvent(TraceEventType.Verbose, 0, "Added known recipient: {0}", bcCert.SubjectDN.ToString());
                }
            }
            if (key != null)
            {
                encryptGenerator.AddKekRecipient("AES", key.BCKey, key.Id);
                trace.TraceEvent(TraceEventType.Verbose, 0, "Added unknown recipient [Algorithm={0}, keyId={1}]", "AES", key.IdString);
            }

            Stream encryptingStream = encryptGenerator.Open(cipher, EteeActiveConfig.Seal.EncryptionAlgorithm.Value);
            trace.TraceEvent(TraceEventType.Verbose, 0, "Create encrypted message (still empty) [EncAlgo={0} ({1})]",
                EteeActiveConfig.Seal.EncryptionAlgorithm.FriendlyName, EteeActiveConfig.Seal.EncryptionAlgorithm.Value);
            try
            {
                clear.CopyTo(encryptingStream);
                trace.TraceEvent(TraceEventType.Verbose, 0, "Message encrypted");
            }
            finally
            {
                encryptingStream.Close();
                trace.TraceEvent(TraceEventType.Verbose, 0, "Recipient infos added");
            }
        }

        protected void Complete(Level level, Stream embedded, Stream signed, X509Certificate2 providedSigner, out TimemarkKey timemarkKey)
        {
            trace.TraceEvent(TraceEventType.Information, 0, "Completing the message with of {0} bytes to level {1}", signed.Length, level);

            //Prepare generator, parser and time-mark Key
            CmsSignedDataStreamGenerator gen = new CmsSignedDataStreamGenerator();
            CmsSignedDataParser parser = new CmsSignedDataParser(signed);
            timemarkKey = new TimemarkKey();

            //preset the digests so we can add the signers afterwards
            gen.AddDigests(parser.DigestOids);

            //Copy the content
            CmsTypedStream signedContent = parser.GetSignedContent();
            Stream contentOut = gen.Open(embedded, parser.SignedContentType.Id, true);
            signedContent.ContentStream.CopyTo(contentOut);

            //Extract the signer info
            SignerInformationStore signerInfoStore = parser.GetSignerInfos();
            IEnumerator signerInfos = signerInfoStore.GetSigners().GetEnumerator();
            if (!signerInfos.MoveNext())
            {
                trace.TraceEvent(TraceEventType.Error, 0, "The message to complete does not contain a signature");
                throw new InvalidMessageException("The message does not contain a signature");
            }
            SignerInformation signerInfo = (SignerInformation)signerInfos.Current;
            if (signerInfos.MoveNext())
            {
                trace.TraceEvent(TraceEventType.Error, 0, "The message to complete does not contain more then one signature");
                throw new InvalidMessageException("The message does contain multiple signatures, which isn't supported");
            }

            //Extract the signing key
            timemarkKey.SignatureValue = signerInfo.GetSignature();

            //Extract the unsigned attributes & signing time
            bool hasSigningTime;
            IDictionary unsignedAttributes = signerInfo.UnsignedAttributes != null ? signerInfo.UnsignedAttributes.ToDictionary() : new Hashtable();
            BC::Asn1.Cms.Attribute singingTimeAttr = signerInfo.SignedAttributes != null ? signerInfo.SignedAttributes[CmsAttributes.SigningTime] : null;
            if (singingTimeAttr == null)
            {
                trace.TraceEvent(TraceEventType.Warning, 0, "The message to complete does not contain a signing time");
                hasSigningTime = false;
                timemarkKey.SigningTime = DateTime.UtcNow;
            }
            else
            {
                hasSigningTime = false;
                timemarkKey.SigningTime = new BC::Asn1.Cms.Time(((DerSet)singingTimeAttr.AttrValues)[0].ToAsn1Object()).Date;
            }
            

            //Extract the signer, if available
            IX509Store embeddedCerts = parser.GetCertificates("Collection");
            if (embeddedCerts != null && embeddedCerts.GetMatches(null).Count > 0)
            {
                //Embedded certs found, we use that
                IEnumerator signerCerts = embeddedCerts.GetMatches(signerInfo.SignerID).GetEnumerator();
                if (!signerCerts.MoveNext()) {
                    trace.TraceEvent(TraceEventType.Error, 0, "The message does contains certificates, but the signing certificate is missing");
                    throw new InvalidMessageException("The message does not contain the signer certificate");
                }
                timemarkKey.Signer = new X509Certificate2(((BC::X509.X509Certificate)signerCerts.Current).GetEncoded());
                trace.TraceEvent(TraceEventType.Verbose, 0, "The message contains certificates, of which {0} is the signer", timemarkKey.Signer.Subject);

                //Add the certs to the new message
                gen.AddCertificates(embeddedCerts);
            }
            else
            {
                //No embedded certs, lets construct it.
                if (providedSigner == null)
                {
                    trace.TraceEvent(TraceEventType.Error, 0, "The provided message does not contain any embedded certificates");
                    throw new InvalidMessageException("The message does not contain any embedded certificates");
                }
                timemarkKey.Signer = providedSigner;
                trace.TraceEvent(TraceEventType.Verbose, 0, "The message does not contains certificates, adding the chain of {0}", timemarkKey.Signer.Subject);

                //Construct the chain of certificates
                Chain chain = timemarkKey.Signer.BuildBasicChain(timemarkKey.SigningTime, extraStore);
                if (chain.ChainStatus.Count(x => x.Status != X509ChainStatusFlags.NoError) > 0)
                {
                    trace.TraceEvent(TraceEventType.Error, 0, "The certification chain of {0} failed with errors", chain.ChainElements[0].Certificate.Subject);
                    throw new InvalidMessageException(string.Format("The certificate chain of the signer {0} fails basic validation", timemarkKey.Signer.Subject));
                }

                List<BC::X509.X509Certificate> senderChainCollection = new List<BC::X509.X509Certificate>();
                foreach (ChainElement ce in chain.ChainElements)
                {
                    trace.TraceEvent(TraceEventType.Verbose, 0, "Adding the certificate {0} to the message", ce.Certificate.Subject);
                    senderChainCollection.Add(DotNetUtilities.FromX509Certificate(ce.Certificate));
                }
                embeddedCerts = X509StoreFactory.Create("CERTIFICATE/COLLECTION", new X509CollectionStoreParameters(senderChainCollection));
                
                //Add the certificates to the new message
                gen.AddCertificates(embeddedCerts);

            }

            //Getting any existing time stamps
            TimeStampToken tst = null;
            BC::Asn1.Cms.Attribute timestampAttr = (BC::Asn1.Cms.Attribute)unsignedAttributes[PkcsObjectIdentifiers.IdAASignatureTimeStampToken];
            if (timestampAttr == null || ((DerSet)timestampAttr.AttrValues).Count == 0)
            {
                //there is no TST
                if ((level & Level.T_Level) == Level.T_Level && timestampProvider != null)
                {
                    //There should be a TST
                    if (DateTime.UtcNow > (timemarkKey.SigningTime + EteeActiveConfig.ClockSkewness + Settings.Default.TimestampGracePeriod))
                    {
                        trace.TraceEvent(TraceEventType.Error, 0, "The message was created on {0}, which is beyond the allows period of {2} to time-stamp", timemarkKey.SigningTime, Settings.Default.TimestampGracePeriod);
                        throw new InvalidMessageException("The message it to old to add a time-stamp");
                    }

                    SHA256 sha = SHA256.Create();
                    byte[] signatureHash = sha.ComputeHash(timemarkKey.SignatureValue);
                    trace.TraceEvent(TraceEventType.Verbose, 0, "SHA-256 hashed the signature value from {0} to {1}", Convert.ToBase64String(timemarkKey.SignatureValue),  Convert.ToBase64String(signatureHash));

                    byte[] rawTst = timestampProvider.GetTimestampFromDocumentHash(signatureHash, "http://www.w3.org/2001/04/xmlenc#sha256");
                    tst = rawTst.ToTimeStampToken();

                    if (!tst.IsMatch(new MemoryStream(timemarkKey.SignatureValue)))
                    {
                        trace.TraceEvent(TraceEventType.Error, 0, "The time-stamp does not correspond to the signature value {0}", Convert.ToBase64String(timemarkKey.SignatureValue));
                        throw new InvalidOperationException("The time-stamp authority did not return a matching time-stamp");
                    }

                    //Don't verify the time-stamp, it is done later

                    //embed TST
                    BC::Asn1.Cms.Attribute signatureTstAttr = new BC::Asn1.Cms.Attribute(PkcsObjectIdentifiers.IdAASignatureTimeStampToken, new DerSet(Asn1Object.FromByteArray(rawTst)));
                    unsignedAttributes[signatureTstAttr.AttrType] = signatureTstAttr;
                    trace.TraceEvent(TraceEventType.Verbose, 0, "Added the time-stamp: {0}", Convert.ToBase64String(rawTst));

                    //The certs are part of the TST, so no need to add them to the CMS
                }
            }
            else
            {
                //There is one, extract it we need it later
                DerSet rawTsts = (DerSet)timestampAttr.AttrValues;
                if (rawTsts.Count > 1)
                {
                    trace.TraceEvent(TraceEventType.Error, 0, "There are {0} signature timestamps present", rawTsts.Count);
                    throw new NotSupportedException("The library does not support more then one time-stamp");
                }

                tst = rawTsts[0].GetEncoded().ToTimeStampToken();
                if (!hasSigningTime)
                {
                    trace.TraceEvent(TraceEventType.Information, 0, "Implicit signing time {0} is replaced with time-stamp time {1}", timemarkKey.SigningTime, tst.TimeStampInfo.GenTime);
                    timemarkKey.SigningTime = tst.TimeStampInfo.GenTime;
                }
                if (tst.TimeStampInfo.GenTime > (timemarkKey.SigningTime + EteeActiveConfig.ClockSkewness + Settings.Default.TimestampGracePeriod))
                {
                    trace.TraceEvent(TraceEventType.Error, 0, "The message was time-stamped on {0}, which is beyond the allows period of {2} from the signing time {1}", tst.TimeStampInfo.GenTime, timemarkKey.SigningTime, Settings.Default.TimestampGracePeriod);
                    throw new InvalidMessageException("The message wasn't timestamped on time");
                }
            }
            

            if ((level & Level.L_Level) == Level.L_Level)
            {
                //Add revocation info
                IList<CertificateList> crls = null;
                IList<BasicOcspResponse> ocsps = null;
                BC::Asn1.Cms.Attribute revocationAttr = (BC::Asn1.Cms.Attribute)unsignedAttributes[PkcsObjectIdentifiers.IdAAEtsRevocationValues];
                if (revocationAttr != null)
                {
                    DerSet revocationInfoSet = (DerSet) revocationAttr.AttrValues;
                    if (revocationInfoSet == null || revocationInfoSet.Count == 0)
                    {
                        RevocationValues revocationInfo = RevocationValues.GetInstance(revocationInfoSet[0]);
                        crls = new List<CertificateList>(revocationInfo.GetCrlVals());
                        trace.TraceEvent(TraceEventType.Verbose, 0, "Found {1} CRL's in the message", crls.Count);
                        ocsps = new List<BasicOcspResponse>(revocationInfo.GetOcspVals());
                        trace.TraceEvent(TraceEventType.Verbose, 0, "Found {1} OCSP's in the message", ocsps.Count);
                    }
                }
                if (crls == null) crls = new List<CertificateList>();
                if (ocsps == null) ocsps = new List<BasicOcspResponse>();

                //Add the message certificate chain revocation info + check if successful
                var extraStore = new X509Certificate2Collection();
                foreach (Org.BouncyCastle.X509.X509Certificate cert in embeddedCerts.GetMatches(null))
                {
                    extraStore.Add(new X509Certificate2(cert.GetEncoded()));
                }
                Chain chain = timemarkKey.Signer.BuildChain(timemarkKey.SigningTime, extraStore, ref crls, ref ocsps);
                if (chain.ChainStatus.Count(x => x.Status != X509ChainStatusFlags.NoError) > 0)
                {
                    trace.TraceEvent(TraceEventType.Error, 0, "The certificate chain of the signer {0} failed with {1} issues: {2}, {3}", timemarkKey.Signer.Subject,
                        chain.ChainStatus.Count, chain.ChainStatus[0].Status, chain.ChainStatus[0].StatusInformation);
                    throw new InvalidMessageException(string.Format("The certificate chain of the signer {0} fails revocation validation", timemarkKey.Signer.Subject));
                }

                //Add the time-stamp certificate chain revocation info + check if successful
                if (tst != null)
                {
                    Timestamp ts = tst.Validate(ref crls, ref ocsps);
                    if (ts.TimestampStatus.Count(x => x.Status != X509ChainStatusFlags.NoError) > 0)
                    {
                        trace.TraceEvent(TraceEventType.Error, 0, "The certificate chain of the time-stamp signer {0} failed with {1} issues: {2}, {3}", ts.CertificateChain.ChainElements[0].Certificate.Subject,
                        ts.TimestampStatus.Count, ts.TimestampStatus[0].Status, ts.TimestampStatus[0].StatusInformation);
                        throw new InvalidMessageException("The embedded time-stamp fails validation");
                    }
                }

                //Embed revocation info
                RevocationValues revocationValues = new RevocationValues(crls, ocsps, null);
                revocationAttr = new BC::Asn1.Cms.Attribute(PkcsObjectIdentifiers.IdAAEtsRevocationValues, new DerSet(revocationValues.ToAsn1Object()));
                unsignedAttributes[revocationAttr.AttrType] = revocationAttr;
                trace.TraceEvent(TraceEventType.Verbose, 0, "Added {0} OCSP's and {1} CRL's to the message", ocsps.Count, crls.Count);
            }

            //Update the unsigned attributes of the signer info
            signerInfo = SignerInformation.ReplaceUnsignedAttributes(signerInfo, new BC::Asn1.Cms.AttributeTable(unsignedAttributes));

            //Copy the signer
            gen.AddSigners(new SignerInformationStore(new SignerInformation[] { signerInfo }));
            
            contentOut.Close();
        }
    }
}
