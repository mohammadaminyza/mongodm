﻿namespace Etherna.MongODM.Serialization.Serializers
{
    public interface IReferenceContainerSerializer : IClassMapContainerSerializer
    {
        bool? UseCascadeDelete { get; }
    }
}
