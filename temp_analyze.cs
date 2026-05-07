using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

class Program
{
    static void Main()
    {
        string dllPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            @".nuget\packages\hslcommunication\11.6.1\lib\netstandard2.1\HslCommunication.dll");

        using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read);
        using var peReader = new PEReader(fs);
        var metadataReader = peReader.GetMetadataReader();

        string[] targets = {
            "MelsecMcNet", "MelsecMcAsciiNet", "SiemensS7Net", "OmronFinsNet", "ModbusTcpNet"
        };

        foreach (var typeDefHandle in metadataReader.TypeDefinitions)
        {
            var typeDef = metadataReader.GetTypeDefinition(typeDefHandle);
            string ns = metadataReader.GetString(typeDef.Namespace);
            string name = metadataReader.GetString(typeDef.Name);

            foreach (var t in targets)
            {
                if (name == t)
                {
                    string baseType = "none";
                    if (!typeDef.BaseType.IsNil)
                    {
                        baseType = GetTypeName(metadataReader, typeDef.BaseType);
                    }
                    Console.WriteLine($"{ns}.{name} -> {baseType}");
                }
            }
        }
    }

    static string GetTypeName(MetadataReader reader, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var td = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
                return reader.GetString(td.Namespace) + "." + reader.GetString(td.Name);
            case HandleKind.TypeReference:
                var tr = reader.GetTypeReference((TypeReferenceHandle)handle);
                return reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
            case HandleKind.TypeSpecification:
                var ts = reader.GetTypeSpecification((TypeSpecificationHandle)handle);
                var sig = ts.DecodeSignature(new SimpleTypeProvider(), null);
                return sig;
            default:
                return handle.Kind.ToString();
        }
    }
}

class SimpleTypeProvider : ISignatureTypeProvider<string, object>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "method ptr";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
    public string GetByReferenceType(string elementType) => elementType + "&";
}
