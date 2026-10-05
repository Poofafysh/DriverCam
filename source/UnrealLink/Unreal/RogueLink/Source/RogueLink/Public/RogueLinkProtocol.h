// The shared-memory protocol between Driving Rogue (the UnrealLink BepInEx plugin) and this Unreal plugin.
// Must match source/UnrealLink/LinkProtocol.cs byte for byte; the static_asserts below pin every offset.
// See LinkProtocol.cs for the meaning of each field. Game data is Unity space (x right, y up, z forward, metres,
// quaternions x y z w) in the player car's body frame; RogueLinkMath.h converts to Unreal.
#pragma once

#include <stdint.h>
#include <stddef.h>

namespace RogueLink
{
	static constexpr const wchar_t* MapName = L"Local\\RogueUnrealLink.State";
	static constexpr const wchar_t* EventName = L"Local\\RogueUnrealLink.Frame";
	static constexpr uint32_t MapSize = 4096;
	static constexpr uint32_t Magic = 0x4B4C5552; // "RULK"
	static constexpr uint32_t Version = 1;
	static constexpr int MaxSlots = 3;

	enum GameFlags : uint32_t
	{
		F_Live = 1, F_DriverView = 2, F_ChaseView = 4, F_Bike = 8, F_Paused = 16, F_DriverCamValid = 32,
		F_HideHead = 64, F_Visible = 128,
	};

	enum UeStatus : int32_t { S_Starting = 0, S_Ready = 1, S_Error = 2 };

#pragma pack(push, 4)
	struct FGameBlock
	{
		uint32_t Magic;          // 0
		uint32_t Version;        // 4
		uint32_t Seq;            // 8
		uint32_t Frame;          // 12
		double Time;             // 16
		float Dt;                // 24
		uint32_t Flags;          // 28
		int32_t WantW, WantH;    // 32, 36
		float Cam[7];            // 40  body-frame camera pos xyz + rot xyzw
		float VFov, Near, Far, Aspect; // 68 72 76 80
		float Body[7];           // 84  body world pos + rot
		float Vel[3];            // 112
		float AngVel[3];         // 124
		float Input[4];          // 136 steer, throttle, brake, speed
		float SunDir[3];         // 152
		float SunColor[3];       // 164
		float Ambient[3];        // 176
		float HeadLook[4];       // 188
		float Engine[8];         // 204
		float DriverCam[26];     // 236
		float Rider[11];         // 340
		char Car[32];            // 384
		char BikeKey[16];        // 416
		int32_t ColorSpace;      // 432
		uint32_t Pid;            // 436
		float CamWorld[7];       // 440
	};                           // 468

	struct FUeBlock
	{
		uint32_t Seq;            // 1024
		uint32_t Version;        // 1028
		uint32_t Pid;            // 1032
		uint32_t Frame;          // 1036
		int32_t W, H;            // 1040 1044
		uint32_t Format;         // 1048 DXGI_FORMAT of the shared ring
		int32_t Slots;           // 1052
		uint64_t Handles[3];     // 1056
		int32_t ReadySlot;       // 1080
		uint32_t SlotFrame[3];   // 1084: game frame each slot was rendered for (0 = test pose, the game never shows it)
		uint32_t HandleGen;      // 1096
		int32_t Status;          // 1100
		float FrameMs, GpuMs, WorkMs, WaitMs; // 1104 1108 1112 1116
		uint32_t LastGameFrame;  // 1120
	};                           // 1124
#pragma pack(pop)

	static constexpr uint32_t GameBlockOffset = 0;
	static constexpr uint32_t UeBlockOffset = 1024;

	static_assert(offsetof(FGameBlock, Seq) == 8, "layout");
	static_assert(offsetof(FGameBlock, Time) == 16, "layout");
	static_assert(offsetof(FGameBlock, Flags) == 28, "layout");
	static_assert(offsetof(FGameBlock, Cam) == 40, "layout");
	static_assert(offsetof(FGameBlock, VFov) == 68, "layout");
	static_assert(offsetof(FGameBlock, Body) == 84, "layout");
	static_assert(offsetof(FGameBlock, Vel) == 112, "layout");
	static_assert(offsetof(FGameBlock, Input) == 136, "layout");
	static_assert(offsetof(FGameBlock, SunDir) == 152, "layout");
	static_assert(offsetof(FGameBlock, HeadLook) == 188, "layout");
	static_assert(offsetof(FGameBlock, Engine) == 204, "layout");
	static_assert(offsetof(FGameBlock, DriverCam) == 236, "layout");
	static_assert(offsetof(FGameBlock, Rider) == 340, "layout");
	static_assert(offsetof(FGameBlock, Car) == 384, "layout");
	static_assert(offsetof(FGameBlock, BikeKey) == 416, "layout");
	static_assert(offsetof(FGameBlock, ColorSpace) == 432, "layout");
	static_assert(offsetof(FGameBlock, CamWorld) == 440, "layout");
	static_assert(sizeof(FGameBlock) == 468, "layout");

	static_assert(UeBlockOffset + offsetof(FUeBlock, Frame) == 1036, "layout");
	static_assert(UeBlockOffset + offsetof(FUeBlock, Handles) == 1056, "layout");
	static_assert(UeBlockOffset + offsetof(FUeBlock, ReadySlot) == 1080, "layout");
	static_assert(UeBlockOffset + offsetof(FUeBlock, SlotFrame) == 1084, "layout");
	static_assert(UeBlockOffset + offsetof(FUeBlock, HandleGen) == 1096, "layout");
	static_assert(UeBlockOffset + offsetof(FUeBlock, FrameMs) == 1104, "layout");
	static_assert(UeBlockOffset + offsetof(FUeBlock, LastGameFrame) == 1120, "layout");
	static_assert(sizeof(FUeBlock) == 100, "layout");
}
