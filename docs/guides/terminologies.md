---
title: Terminologies
---

## Environments

### Managed

Managed is .NET managed code which is used by C++ core. The memory and lifetime of objects are managed automatically by the runtime (garbage collection, type safety, metadata, etc.). The managed environment hosts and runs the plugins.

### Native

Native is the unmanaged side of SwiftlyS2, to be more exact, the C++ core. Memory management and execution are done manually (new/delete, RAII, etc.). The C++ core is the environment that initialized the managed environment for plugins.

## Players

All players in CS2 are split between a player controller & a player pawn.

### Controllers

The player controller represents the player on the server. It holds informations about the player, such as: name, steamid, etc. Every controller has assigned a pawn. In the entity system, it holds the indexes from 1 to 64. The controller is a virtual entity that represents a player abstraction, so stuff such like: health, positioning, velocity can't be modified from over here.

### Pawn

The player pawn represents the character of the player on the server. It holds informations about the character in game, such as: health, position, velocity, color, weapons and so on. Every pawn has a controller assigned to it.

### Slot/PlayerId

A slot on the server represents the controller's entity index - 1, so for example, if the controller entity index is 7, the player has assigned slot 6. Most of the game events are referring to the slot as `userid` or `playerid`.

### Player Object

The player object is an object created by us to give the possibility to developers to have faster accessing to game elements such as: fetching controller, fetching pawn, teleporting pawn, sending messages and so on. This allows us to give efficient API for a faster development experience.

## Entities

The entity system has a maximum capacity of 16384 indexes. Each entity has assigned a specific index.

Ranges:
- `1-64`: Player Controllers (controller index = playerid + 1)
- `65-16383`: Entites

### Handles

An entity handle is a safe container in which an entity is stored. It stores in the container the pointer of the entity and the entity index. Using that, you can ensure that your entity is safely stored and that it can be checked against a list from the entity system to see if the said entity is still valid.

### Kinds

#### Temporary

Temporary entities are spawn every round, at the beginning of the round and are removed at the end of the round.

#### Permanent

Permanent entities are spawned with the map, and those are not removed once the round ends.