// The game's frame as Blueprint data (URogueLinkSubsystem::GetState). Unreal units and axes: centimetres, X forward,
// Y right, Z up, in the player car's body frame (the car body sits at the world origin of the link level).
#pragma once

#include "CoreMinimal.h"
#include "RogueLinkTypes.generated.h"

USTRUCT(BlueprintType)
struct ROGUELINK_API FRogueLinkState
{
	GENERATED_BODY()

	/** A game frame arrived in the last second. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bGameConnected = false;
	/** In a race with the player's car (the avatar is wanted). */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bLive = false;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bDriverView = false;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bChaseView = false;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bBike = false;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bPaused = false;
	/** Eye / wheel / seat below came from DriverCam for this car. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bSeatValid = false;
	/** The camera is inside the head (hide head and helmet). */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bHideHead = false;
	/** The game shows the layer. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") bool bVisible = false;

	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") int32 GameFrame = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") double GameTime = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink") float GameDeltaTime = 0;

	/** The game camera in the body frame. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Camera") FTransform Camera;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Camera") float VerticalFovDeg = 60;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Camera") float HorizontalFovDeg = 90;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Camera") float NearCm = 5;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Camera") float Aspect = 16.f / 9.f;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Camera") FIntPoint RenderSize = FIntPoint(0, 0);
	/** The game camera in the game world (converted axes, cm). */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Camera") FTransform CameraWorld;

	/** The car body in the game world (converted axes, cm). */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") FTransform BodyWorld;
	/** Body-frame velocity, cm/s, and angular velocity, rad/s. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") FVector Velocity = FVector::ZeroVector;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") FVector AngularVelocity = FVector::ZeroVector;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") float Steer = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") float Throttle = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") float Brake = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") float SpeedMps = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") FString CarId;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") FString BikeKey;

	/** Engine (EngineAudio): rpm, gear (0 = first), live. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") float Rpm = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") int32 Gear = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Car") bool bEngineLive = false;

	/** HeadLook: yaw / pitch in degrees. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") float HeadYaw = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") float HeadPitch = 0;

	/** DriverCam seat data (body frame, cm). */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") FVector Eye = FVector::ZeroVector;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") FVector WheelPivot = FVector::ZeroVector;
	/** Unspun wheel frame: X toward the dash, Y the driver's right, Z up. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") FRotator WheelRotation = FRotator::ZeroRotator;
	FQuat WheelQuat = FQuat::Identity;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") float RimRadiusCm = 18.5f;
	/** Wheel spin in degrees for the current steer (-Steer * SteerAngle). */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") float WheelSpinDeg = 0;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") float SteerAngleDeg = 120;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") FVector SeatCushion = FVector::ZeroVector;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Driver") FVector SeatBack = FVector::ZeroVector;

	/** Light, body frame: the direction the sunlight travels, its colour (linear, times intensity), ambient. */
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Light") FVector SunDirection = FVector(0.3, 0.2, -1);
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Light") FLinearColor SunColor = FLinearColor::White;
	UPROPERTY(BlueprintReadOnly, Category = "RogueLink|Light") FLinearColor Ambient = FLinearColor(0.3f, 0.3f, 0.3f);
};
