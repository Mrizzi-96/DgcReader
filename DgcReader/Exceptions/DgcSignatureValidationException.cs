﻿using DgcReader.Models;
using System;
using System.Runtime.Serialization;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace DgcReader.Exceptions
{
    [Serializable]
    public class DgcSignatureValidationException : DgcException
    {
        /// <summary>
        /// The signature validation result
        /// </summary>
        public SignatureValidationResult? Result { get; set; }

        public DgcSignatureValidationException(string message,
            SignatureValidationResult? result = null) :
            base(message)
        {
            Result = result;
        }

        public DgcSignatureValidationException(string message,
            Exception innerException,
            SignatureValidationResult? result = null) :
            base(message, innerException)
        {
            Result = result;
        }

        public DgcSignatureValidationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }


    [Serializable]
    public class DgcUnknownSignerException : DgcSignatureValidationException
    {
        /// <summary>
        /// The certificate key identifier used for searching the public key
        /// </summary>
        public string? Kid => Result?.CertificateKid;

        public DgcUnknownSignerException(string message, SignatureValidationResult? result = null)
            : base(message, result: result)
        {
        }

        public DgcUnknownSignerException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    [Serializable]
    public class DgcSignatureExpiredException : DgcSignatureValidationException
    {
        public DgcSignatureExpiredException(string message, SignatureValidationResult? result = null) :
            base(message, result: result)
        {

        }

        public DgcSignatureExpiredException(string message,
            Exception innerException,
            SignatureValidationResult? result = null) :
            base(message, innerException, result: result)
        {

        }

        public DgcSignatureExpiredException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
