# Script commands

Mission scripts (`Data\Script\Mission\<mission>\*.scp`) are written in the game's C-like language. This page lists
the commands that OpenFD understands (the same list as `src\OpenFD.Editor\ScriptCommands.cs`, shown in the editor's
script window), with a short description of each one.

Conventions:

- `entity` is an entity id, usually obtained with `ENT_FindByName("name")`. Most `ENT_`, `FIR_` and `IA_` commands
  also accept a group (from the level or made with `ENT_GroupCreate`): the command then applies to every member.
- `variable` means the game writes the result into the script variable you pass.
- Positions and distances are in centimetres, angles in degrees, durations in seconds.
- Booleans are integers: `0` = false, `1` = true.

## Animation

| Command | Description |
|---|---|
| `ANM_Play(entity, anim, speed, loop)` | Plays an animation (`Gfx\Anim\…`) on an object: doors and gates that open, moving parts. `loop` = 1 repeats it. |
| `ANM_Stop(entity)` | Stops the animation played by `ANM_Play`. |

## Camera

| Command | Description |
|---|---|
| `CAM_Cinematic(on)` | 1: enters cinematic mode (the player cannot control the camera); 0: leaves it. |
| `CAM_CinematicSetToWayPoint(waypoint, seconds)` | During a cinematic, moves the camera to a waypoint over the given time. |
| `CAM_ModeCinematic()` | Same as `CAM_Cinematic(1)`. |
| `CAM_ModePlay()` | Gives the camera back to the player. |
| `CAM_GetInertia()` | Returns the camera inertia. |
| `CAM_SetInertia(value)` | Sets the camera inertia (smoothness of its moves). |
| `CAM_SetToWayPoint(waypoint, seconds)` | Moves the camera to a waypoint over the given time. |
| `CAM_TeleportToWayPoint(waypoint)` | Puts the camera on a waypoint at once. |

## Entities

| Command | Description |
|---|---|
| `ENT_FindByName(name)` | Returns the id of the unit, object, waypoint, trigger or group with this name (0 if none). |
| `ENT_GetName(entity, variable)` | Name of an entity. |
| `ENT_GetType(entity)` | Kind of entity in the level (placeable, unit, hotpoint, waypoint…). |
| `ENT_GetPosition(entity, x, y)` | Writes the entity's X and Y into the two variables. |
| `ENT_GetZ(entity)` | Returns the entity's height. |
| `ENT_SetPosition(entity, x, y)` | Moves a unit to this point at once. |
| `ENT_GetOrientation(entity, variable)` | Heading of the entity, in degrees. |
| `ENT_GetOrientationToEntity(from, to, variable)` | Heading from one entity towards another. |
| `ENT_SetOrientation(entity, degrees)` | Turns the entity. |
| `ENT_GetVisibility(entity)` | 1 if shown, 0 if hidden. |
| `ENT_SetVisibility(entity, visible)` | Shows (1) or hides (0) an entity; hidden obstacles stop blocking the way. |
| `ENT_GetState(entity)` | State of a door or switch: `"Open"`, `"Close"` or `"Broken"`. |
| `ENT_SwitchState(entity)` | Toggles a door, gate or switch (open ↔ closed). |
| `ENT_Destroy(entity)` | Removes the entity from the game. |
| `ENT_GetDamage(entity)` | Life points left of an object or vehicle. |
| `ENT_SetDamage(entity, life)` | Sets its life points (0 destroys it; an explosive object explodes). |
| `ENT_AddDamage(entity, value)` | Adds life points (a negative value damages it); on a fireman, changes his stamina. |
| `ENT_SetFireDamageCoef(entity, coef)` | How fast fire damages this object (0 = fireproof). |
| `ENT_GetStamina(unit)` | Stamina of a fireman, in % (100 = fresh). |
| `ENT_SetStamina()` | Accepted, no effect. |
| `ENT_GetReserve(unit)` | Water or agent left in a unit (sum for a group). |
| `ENT_SetReserve(unit, value)` | Sets the water or agent left. |
| `ENT_GetUnitState(unit)` | State of a unit: 4 if destroyed, otherwise 0. |
| `ENT_GetUnitType(unit)` | Type of a unit (fireman, vehicle…). |
| `ENT_GetNbPassengers(vehicle)` | Number of people inside a vehicle. |
| `ENT_GetCapacityTarget(entity, capacity)` | 1 if this capacity (for instance `"CouldBeUse"`) is enabled on the object. |
| `ENT_SetCapacityTarget(entity, capacity, on)` | Enables or disables a capacity of an object (lets the player use it or not). |
| `ENT_GetPropertyValueF32(entity, section, key)` | Reads a decimal value from the entity's class. |
| `ENT_GetPropertyValueS32(entity, section, key)` | Reads an integer value from the entity's class. |
| `ENT_GetPropertyValueString(entity, section, key)` | Reads a text value from the entity's class. |
| `ENT_SetPropertyValueF32(entity, section, key, value)` | Changes a decimal value of the entity's class. |
| `ENT_SetPropertyValueS32(entity, section, key, value)` | Changes an integer value of the entity's class. |
| `ENT_SetPropertyValueString(entity, section, key, value)` | Changes a text value of the entity's class. |
| `ENT_SetWindPower(value)` | Wind strength (fire spreads faster with more wind). |
| `ENT_ActivateTeleport(entity, on)` | Enables or disables a staircase (teleporter). |
| `ENT_CreateUnitPath(class, name, x, y, z)` | Creates a unit of this class at this point; returns its id. |

## Groups

| Command | Description |
|---|---|
| `ENT_GroupCreate(name)` | Creates an empty group (it can then be found with `ENT_FindByName`). |
| `ENT_GroupAddEntity(group, entity)` | Adds an entity to a group. |
| `ENT_GroupRemEntity(group, entity)` | Removes an entity from a group. |
| `ENT_GroupRemoveEntity(group, entity)` | Same as `ENT_GroupRemEntity`. |
| `ENT_GroupGetNbEntities(group)` | Number of entities in the group. |
| `ENT_GroupGetEntityAt(group, index)` | Entity at this position in the group (from 0). |
| `ENT_IsEntityInGroup(entity, group)` | 1 if the entity belongs to the group. |

## Fire

`hotpoint` is a fire (hotpoint) or a group of fires.

| Command | Description |
|---|---|
| `FIR_HPEnable(hotpoint, on)` | Enables or disables fires; disabling a burning electrical fire cuts the power (it becomes an ordinary fire). |
| `FIR_HPIsEnable(hotpoint)` | 1 if enabled. |
| `FIR_HPGetIntensity(hotpoint)` | Total intensity of the lit fires. |
| `FIR_HPGetActiveIntensity(hotpoint)` | Total intensity of the fires that are really burning. |
| `FIR_HPSetIntensity(hotpoint, value)` | Sets the intensity (a value above 0 lights the fire). |
| `FIR_HPAddIntensity(hotpoint, value)` | Adds intensity (negative to reduce it). |
| `FIR_HPGetGrowthRate(hotpoint)` | How fast the fire grows. |
| `FIR_HPSetGrowthRate(hotpoint, value)` | Sets how fast the fire grows. |
| `FIR_HPMultGrowthRate(hotpoint, factor)` | Multiplies the growth speed (0.5 = twice slower). |
| `FIR_HPSetGrowthTime(hotpoint, seconds)` | Time for the fire to reach its maximum intensity. |
| `FIR_SPGetIntensity()` | Smoke intensity (not simulated yet: returns 0). |
| `FIR_SPAddIntensity()` | Smoke intensity (not simulated yet: no effect). |

## Unit orders (AI)

| Command | Description |
|---|---|
| `IA_CmdMoveTo(unit, x, y, mode)` | Sends the unit to this point (it opens closed doors on its way). |
| `IA_CmdAttackFire(unit, hotpoint)` | The unit attacks this fire. |
| `IA_CmdEmbark(unit, vehicle)` | The unit gets into the vehicle. |
| `IA_CmdUse(unit, entity)` | The unit uses an object (switch, valve, door…). |
| `IA_CmdStop(unit)` | The unit stops (repeat it in the main loop to hold it in place). |

## Level, objectives and reinforcements

| Command | Description |
|---|---|
| `LEV_AddObjectiveBoolean(text)` | Adds a yes/no objective; returns its number. |
| `LEV_AddObjectiveProgress(text, start, goal)` | Adds an objective with a progress bar. |
| `LEV_AddObjectiveIterate(text, start, goal)` | Adds a counting objective ("3 / 5"). |
| `LEV_UpdateObjective(objective, value)` | Updates the value of a progress or counting objective. |
| `LEV_ValidateObjective(objective, success)` | Ends an objective: 1 = succeeded, 0 = failed. |
| `LEV_GetObjectiveStatus(objective)` | State of an objective (in progress, succeeded, failed). |
| `LEV_SetObjectivePos(objective, x, y)` | Puts the objective's marker on the map at this point. |
| `LEV_ClearAllObjectives()` | Removes all objectives. |
| `LEV_ActivateObjectivesPanel(on)` | Shows (1) or hides (0) the objectives panel. |
| `LEV_TriggerVictory()` | The mission is won. |
| `LEV_TriggerDefeat()` | The mission is lost. |
| `LEV_SetDebriefText(text)` | Text shown on the end-of-mission screen. |
| `LEV_AddScoringLine(text, value, points)` | Adds a line to the end-of-mission score. |
| `LEV_GetDifficultyLevel()` | Difficulty: 0 easy, 1 normal, 2 hard. |
| `LEV_GetFireSizePercent()` | Size of the burning fire as a fraction (0 to 1) of the largest possible fire of the map. |
| `LEV_GetNumFiremen()` | Number of firemen in the mission. |
| `LEV_GetFiremenWounds()` | Number of firemen wounds. |
| `LEV_GetNumKilledFiremen()` | Firemen killed (always 0 in OpenFD). |
| `LEV_GetNumKilledVehicles()` | Vehicles destroyed (always 0 in OpenFD). |
| `LEV_GetVehicleDamages()` | Damage taken by the vehicles (always 0 in OpenFD). |
| `LEV_GetNumVictims()` | Number of civilians who died. |
| `LEV_IsFiremanSupplierRemaining()` | 1 if a supply vehicle (water, agent) is still available. |
| `LEV_IsHealVehicleRemaining()` | 1 if an ambulance is still available. |
| `LEV_CallInitialDispatch()` | Sends the first teams of the mission. |
| `LEV_CallReinforcement(n)` | Sends reinforcement number n at once. |
| `LEV_GrantReinforcement()` | Lets the player call the next reinforcement. |
| `LEV_GrantSpecificReinforcement(n)` | Lets the player call reinforcement n. |
| `LEV_GrantReinforcementSet(r1, r2, …)` | Offers a choice: the reinforcements marked 1 are proposed together, the player picks one. |
| `LEV_GrantAirSupport()` | Gives the player the water bomber (air support). |
| `LEV_RoofVisibility(mesh, visible)` | Shows (1) or removes (0) a roof of the scenery. |
| `LEV_MaskVisibility(mesh, visible)` | Shows (1) or removes (0) a roof mask. |

## Panels, messages and sound

| Command | Description |
|---|---|
| `PNL_AddMessage(text, picture, duration, priority)` | Shows a message (with a portrait) in the message panel. |
| `PNL_ClearAllMessages()` | Removes all messages. |
| `PNL_PrintSubtitle(text)` | Shows a subtitle (used in cinematics). |
| `PNL_ClearSubtitle()` | Removes the subtitle. |
| `PNL_StartCutscene(name)` | Plays a cutscene (video). |
| `PNL_RemoveAllFocus()` | Accepted, no effect. |
| `PANEL_SetInterfaceMode(mode)` | Switches the interface mode (`"SCORING"`: end-of-mission score screen). |
| `SND_PlaySound2D(label)` | Plays a sound or voice (not placed in the scene). |
| `SND_PlayStaticSound(label)` | Plays a background sound. |
| `SND_StopStaticSound()` | Accepted, no effect. |

## Script engine

| Command | Description |
|---|---|
| `SCR_GetVarS32(name)` | Reads a global integer variable (shared by all script files). |
| `SCR_GetVarF32(name)` | Reads a global decimal variable. |
| `SCR_GetVarString(name)` | Reads a global text variable. |
| `SCR_SetVarS32(name, value)` | Writes a global integer variable. |
| `SCR_SetVarF32(name, value)` | Writes a global decimal variable. |
| `SCR_SetVarString(name, value)` | Writes a global text variable. |
| `SCR_VarExists(name)` | 1 if the global variable exists. |
| `SCR_RunDelayed(function, seconds)` | Calls a script function after a delay. |
| `SCR_RunDelayedArgs(function, seconds, a, b, c)` | Same, with up to three arguments. |
| `SCR_RunDelayedEntity(function, seconds, entity)` | Same, with an entity as argument. |
| `SCR_RunDelayedMultiArgs(function, seconds, …)` | Same, with any number of arguments. |
| `SCR_IsRunning(function)` | 1 if this function is waiting to run (delayed) or running. |

## Utilities and maths

| Command | Description |
|---|---|
| `UTL_GetGameTime()` | Game time since the start of the mission, in seconds. |
| `UTL_GetDeltaGameTime()` | Time between two runs of the mission's main loop, in seconds. |
| `UTL_GetRandom()` | Random number between 0 and 1. |
| `sin(angle)`, `cos(angle)` | Sine and cosine (angle in radians). |
| `sqrt(value)` | Square root. |
| `mod(a, b)` | Remainder of a divided by b. |
