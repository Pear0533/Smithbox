// ------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
// ------------------------------------------------------------------------------

using System;
using HexaGen.Runtime;
using System.Numerics;

namespace Hexa.NET.ImGuiNodeEditor
{
	/// <summary>
	/// ------------------------------------------------------------------------------<br/>
	/// </summary>
	[Flags]
	public enum SaveReasonFlags : uint
	{
		None = unchecked((int)0x00000000),
		Navigation = unchecked((int)0x00000001),
		Position = unchecked((int)0x00000002),
		Size = unchecked((int)0x00000004),
		Selection = unchecked((int)0x00000008),
		AddNode = unchecked((int)0x00000010),
		RemoveNode = unchecked((int)0x00000020),
		User = unchecked((int)0x00000040),
	}
}
