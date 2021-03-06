﻿using System.Xml.Serialization;

namespace Twingly.Search.Client.Infrastructure
{
    [XmlRoot(ElementName = "operationResult")]
    public class OperationResult
    {
        [XmlAttribute(AttributeName = "resultType")]
        public string ResultType { get; set; }

        [XmlText]
        public string Text { get; set; }
    }
}
