﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using DMD = dnlib.DotNet;

namespace dnSpy.Debugger.DotNet.Metadata.Impl {
	[Serializable]
	sealed class CABlobParserException : Exception {
		public CABlobParserException(string message) : base(message) { }
	}

	enum SerializationType : byte {
		Undefined	= 0,
		Boolean		= DMD.ElementType.Boolean,
		Char		= DMD.ElementType.Char,
		I1			= DMD.ElementType.I1,
		U1			= DMD.ElementType.U1,
		I2			= DMD.ElementType.I2,
		U2			= DMD.ElementType.U2,
		I4			= DMD.ElementType.I4,
		U4			= DMD.ElementType.U4,
		I8			= DMD.ElementType.I8,
		U8			= DMD.ElementType.U8,
		R4			= DMD.ElementType.R4,
		R8			= DMD.ElementType.R8,
		String		= DMD.ElementType.String,
		SZArray		= DMD.ElementType.SZArray,
		Type		= 0x50,
		TaggedObject= 0x51,
		Field		= 0x53,
		Property	= 0x54,
		Enum		= 0x55,
	}

	struct DmdCustomAttributeReader : IDisposable {
		readonly DmdModule module;
		readonly DmdDataStream reader;
		readonly DmdConstructorInfo ctor;
		const int MAX_RECURSION_COUNT = 100;
		int recursionCounter;

		public static DmdCustomAttributeData Read(DmdModule module, DmdDataStream stream, DmdConstructorInfo ctor) {
			using (var reader = new DmdCustomAttributeReader(module, stream, ctor)) {
				try {
					return reader.Read();
				}
				catch (CABlobParserException) {
				}
				catch (ResolveException) {
				}
				catch (IOException) {
				}
				return null;
			}
		}

		DmdCustomAttributeReader(DmdModule module, DmdDataStream reader, DmdConstructorInfo ctor) {
			this.module = module;
			this.reader = reader;
			this.ctor = ctor;
			recursionCounter = 0;
		}

		bool IncrementRecursionCounter() {
			if (recursionCounter >= MAX_RECURSION_COUNT)
				return false;
			recursionCounter++;
			return true;
		}
		void DecrementRecursionCounter() => recursionCounter--;

		DmdCustomAttributeData Read() {
			var ctorParams = ctor.GetReadOnlyParameters();
			bool isEmpty = ctorParams.Count == 0 && reader.Position == reader.Length;
			if (!isEmpty && reader.ReadUInt16() != 1)
				throw new CABlobParserException("Invalid CA blob prolog");

			var ctorArgs = new DmdCustomAttributeTypedArgument[ctorParams.Count];
			for (int i = 0; i < ctorArgs.Length; i++)
				ctorArgs[i] = ReadFixedArg(FixTypeSig(ctorParams[i].ParameterType));

			// Some tools don't write the next ushort if there are no named arguments.
			int numNamedArgs = reader.Position == reader.Length ? 0 : reader.ReadUInt16();
			var namedArgs = ReadNamedArguments(numNamedArgs);

			return new DmdCustomAttributeData(ctor, new ReadOnlyCollection<DmdCustomAttributeTypedArgument>(ctorArgs), new ReadOnlyCollection<DmdCustomAttributeNamedArgument>(namedArgs));
		}

		DmdCustomAttributeNamedArgument[] ReadNamedArguments(int numNamedArgs) {
			var namedArgs = new List<DmdCustomAttributeNamedArgument>(numNamedArgs);
			for (int i = 0; i < numNamedArgs; i++) {
				if (reader.Position == reader.Length)
					break;
				namedArgs.Add(ReadNamedArgument());
			}
			return namedArgs.ToArray();
		}

		DmdType FixTypeSig(DmdType type) => type.WithoutCustomModifiers();

		DmdCustomAttributeTypedArgument ReadFixedArg(DmdType argType) {
			if (!IncrementRecursionCounter())
				throw new CABlobParserException("Stack overflow");
			if ((object)argType == null)
				throw new CABlobParserException("null argType");
			DmdCustomAttributeTypedArgument result;

			if (argType.IsSZArray)
				result = ReadArrayArgument(argType);
			else
				result = ReadElem(argType);

			DecrementRecursionCounter();
			return result;
		}

		DmdCustomAttributeTypedArgument ReadElem(DmdType argType) {
			if ((object)argType == null)
				throw new CABlobParserException("null argType");
			var value = ReadValue(ToSerializationType(argType), argType, out var realArgType);
			if ((object)realArgType == null)
				throw new CABlobParserException("Invalid arg type");

			// One example when this is true is when prop/field type is object and
			// value type is string[]
			if (value is DmdCustomAttributeTypedArgument arg)
				return arg;

			return new DmdCustomAttributeTypedArgument(realArgType, value);
		}

		object ReadValue(SerializationType etype, DmdType argType, out DmdType realArgType) {
			if (!IncrementRecursionCounter())
				throw new CABlobParserException("Stack overflow");

			object result;
			switch (etype) {
			case SerializationType.Boolean:
				realArgType = module.AppDomain.System_Boolean;
				result = reader.ReadByte() != 0;
				break;

			case SerializationType.Char:
				realArgType = module.AppDomain.System_Char;
				result = (char)reader.ReadUInt16();
				break;

			case SerializationType.I1:
				realArgType = module.AppDomain.System_SByte;
				result = reader.ReadSByte();
				break;

			case SerializationType.U1:
				realArgType = module.AppDomain.System_Byte;
				result = reader.ReadByte();
				break;

			case SerializationType.I2:
				realArgType = module.AppDomain.System_Int16;
				result = reader.ReadInt16();
				break;

			case SerializationType.U2:
				realArgType = module.AppDomain.System_UInt16;
				result = reader.ReadUInt16();
				break;

			case SerializationType.I4:
				realArgType = module.AppDomain.System_Int32;
				result = reader.ReadInt32();
				break;

			case SerializationType.U4:
				realArgType = module.AppDomain.System_UInt32;
				result = reader.ReadUInt32();
				break;

			case SerializationType.I8:
				realArgType = module.AppDomain.System_Int64;
				result = reader.ReadInt64();
				break;

			case SerializationType.U8:
				realArgType = module.AppDomain.System_UInt64;
				result = reader.ReadUInt64();
				break;

			case SerializationType.R4:
				realArgType = module.AppDomain.System_Single;
				result = reader.ReadSingle();
				break;

			case SerializationType.R8:
				realArgType = module.AppDomain.System_Double;
				result = reader.ReadDouble();
				break;

			case SerializationType.String:
				realArgType = module.AppDomain.System_String;
				result = ReadUTF8String();
				break;

			// It's ET.ValueType if it's eg. a ctor enum arg type
			case (SerializationType)DMD.ElementType.ValueType:
				if ((object)argType == null)
					throw new CABlobParserException("Invalid element type");
				realArgType = argType;
				result = ReadEnumValue(GetEnumUnderlyingType(argType));
				break;

			// It's ET.Object if it's a ctor object arg type
			case (SerializationType)DMD.ElementType.Object:
			case SerializationType.TaggedObject:
				realArgType = ReadFieldOrPropType();
				if (realArgType.IsSZArray)
					result = ReadArrayArgument(realArgType);
				else
					result = ReadValue(ToSerializationType(realArgType), realArgType, out var tmpType);
				break;

			// It's ET.Class if it's eg. a ctor System.Type arg type
			case (SerializationType)DMD.ElementType.Class:
				if (argType == module.AppDomain.System_Type) {
					result = ReadValue(SerializationType.Type, argType, out realArgType);
					break;
				}
				else if (argType == module.AppDomain.System_String) {
					result = ReadValue(SerializationType.String, argType, out realArgType);
					break;
				}
				else if (argType == module.AppDomain.System_Object) {
					result = ReadValue(SerializationType.TaggedObject, argType, out realArgType);
					break;
				}

				// Assume it's an enum that couldn't be resolved
				realArgType = argType;
				result = ReadEnumValue(null);
				break;

			case SerializationType.Type:
				realArgType = argType;
				result = ReadType(true);
				break;

			case SerializationType.Enum:
				realArgType = ReadType(false);
				result = ReadEnumValue(GetEnumUnderlyingType(realArgType));
				break;

			default:
				throw new CABlobParserException("Invalid element type");
			}

			DecrementRecursionCounter();
			return result;
		}

		static SerializationType ToSerializationType(DmdType type) {
			if ((object)type == null)
				return SerializationType.Undefined;
			if (type.IsSZArray)
				return SerializationType.SZArray;
			return ToSerializationType(DmdType.GetTypeCode(type));
		}

		static SerializationType ToSerializationType(TypeCode typeCode) {
			switch (typeCode) {
			case TypeCode.Boolean:	return SerializationType.Boolean;
			case TypeCode.Char:		return SerializationType.Char;
			case TypeCode.SByte:	return SerializationType.I1;
			case TypeCode.Byte:		return SerializationType.U1;
			case TypeCode.Int16:	return SerializationType.I2;
			case TypeCode.UInt16:	return SerializationType.U2;
			case TypeCode.Int32:	return SerializationType.I4;
			case TypeCode.UInt32:	return SerializationType.U4;
			case TypeCode.Int64:	return SerializationType.I8;
			case TypeCode.UInt64:	return SerializationType.U8;
			case TypeCode.String:	return SerializationType.String;
			default:				return SerializationType.Undefined;
			}
		}

		object ReadEnumValue(DmdType underlyingType) {
			if ((object)underlyingType != null) {
				var typeCode = DmdType.GetTypeCode(underlyingType);
				if (typeCode < TypeCode.Boolean || typeCode > TypeCode.UInt64)
					throw new CABlobParserException("Invalid enum underlying type");
				return ReadValue(ToSerializationType(typeCode), underlyingType, out var realArgType);
			}

			throw new CABlobParserException("Couldn't resolve enum type");
		}

		DmdType ReadType(bool canReturnNull) {
			var name = ReadUTF8String();
			if (canReturnNull && name == null)
				return null;
			var type = DmdTypeNameParser.Parse(module, name ?? string.Empty, ctor.ReflectedType.GetReadOnlyGenericArguments());
			if ((object)type == null)
				throw new CABlobParserException("Could not parse type");
			return type;
		}

		static DmdType GetEnumUnderlyingType(DmdType type) {
			if ((object)type == null)
				throw new CABlobParserException("null enum type");
			var td = type.ResolveNoThrow();
			if ((object)td == null)
				return null;
			if (!td.IsEnum)
				throw new CABlobParserException("Not an enum");
			return td.GetEnumUnderlyingType().WithoutCustomModifiers();
		}

		DmdCustomAttributeTypedArgument ReadArrayArgument(DmdType arrayType) {
			if (!arrayType.IsSZArray)
				throw new ArgumentException();
			if (!IncrementRecursionCounter())
				throw new CABlobParserException("Stack overflow");

			object argValue;
			int arrayCount = reader.ReadInt32();
			if (arrayCount == -1)// -1 if it's null
				argValue = null;
			else if (arrayCount < 0)
				throw new CABlobParserException("Array is too big");
			else {
				var array = new DmdCustomAttributeTypedArgument[arrayCount];
				argValue = array;
				var elemType = FixTypeSig(arrayType.GetElementType());
				for (int i = 0; i < array.Length; i++)
					array[i] = ReadFixedArg(elemType);
			}

			DecrementRecursionCounter();
			return new DmdCustomAttributeTypedArgument(arrayType, argValue);
		}

		DmdCustomAttributeNamedArgument ReadNamedArgument() {
			bool isField;
			switch ((SerializationType)reader.ReadByte()) {
			case SerializationType.Property:isField = false; break;
			case SerializationType.Field:	isField = true; break;
			default: throw new CABlobParserException("Named argument is not a field/property");
			}

			var fieldPropType = ReadFieldOrPropType();
			var name = ReadUTF8String();
			var argument = ReadFixedArg(fieldPropType);

			DmdMemberInfo memberInfo;
			if (isField) {
				var field = ctor.ReflectedType.GetField(name);
				if ((object)field == null || !DmdMemberInfoEqualityComparer.Default.Equals(field.FieldType.WithoutCustomModifiers(), fieldPropType))
					memberInfo = null;
				else
					memberInfo = field;
			}
			else {
				var property = ctor.ReflectedType.GetProperty(name);
				if ((object)property == null || !DmdMemberInfoEqualityComparer.Default.Equals(property.PropertyType.WithoutCustomModifiers(), fieldPropType))
					memberInfo = null;
				else
					memberInfo = property;
			}

			if ((object)memberInfo == null)
				throw new ResolveException($"Couldn't resolve CA {(isField ? "field" : "property")}: {name}");

			return new DmdCustomAttributeNamedArgument(memberInfo, argument);
		}

		DmdType ReadFieldOrPropType() {
			if (!IncrementRecursionCounter())
				throw new CABlobParserException("Stack overflow");
			DmdType result;
			switch ((SerializationType)reader.ReadByte()) {
			case SerializationType.Boolean: result = module.AppDomain.System_Boolean; break;
			case SerializationType.Char:	result = module.AppDomain.System_Char; break;
			case SerializationType.I1:		result = module.AppDomain.System_SByte; break;
			case SerializationType.U1:		result = module.AppDomain.System_Byte; break;
			case SerializationType.I2:		result = module.AppDomain.System_Int16; break;
			case SerializationType.U2:		result = module.AppDomain.System_UInt16; break;
			case SerializationType.I4:		result = module.AppDomain.System_Int32; break;
			case SerializationType.U4:		result = module.AppDomain.System_UInt32; break;
			case SerializationType.I8:		result = module.AppDomain.System_Int64; break;
			case SerializationType.U8:		result = module.AppDomain.System_UInt64; break;
			case SerializationType.R4:		result = module.AppDomain.System_Single; break;
			case SerializationType.R8:		result = module.AppDomain.System_Double; break;
			case SerializationType.String:	result = module.AppDomain.System_String; break;
			case SerializationType.SZArray: result = ReadFieldOrPropType().MakeArrayType(); break;
			case SerializationType.Type:	result = module.AppDomain.System_Type; break;
			case SerializationType.TaggedObject: result = module.AppDomain.System_Object; break;
			case SerializationType.Enum:	result = ReadType(false); break;
			default: throw new CABlobParserException("Invalid type");
			}
			DecrementRecursionCounter();
			return result;
		}

		string ReadUTF8String() {
			byte b = reader.ReadByte();
			if (b == 0xFF)
				return null;
			uint len = reader.ReadCompressedUInt32(b);
			if (len == 0)
				return string.Empty;
			return Encoding.UTF8.GetString(reader.ReadBytes((int)len));
		}

		public void Dispose() => reader?.Dispose();
	}
}
