#region Copyright & License Information
/*
 * Copyright 2021-2025 The OpenHV Developers (see CREDITS)
 * This file is part of OpenHV, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.HV.Traits
{
	// What to do when parent is killed or mind controlled
	public enum SpawnerChildDisposal
	{
		DoNothing,
		KillChildren,
		GiveChildrenToAttacker
	}

	public class BaseSpawnerChildEntry
	{
		public string ActorName = null;
		public Actor Actor = null;
		public BaseSpawnerChild SpawnerChild = null;

		public bool IsValid { get { return Actor != null && !Actor.IsDead; } }
	}

	[Desc("This actor can spawn actors.")]
	public class BaseSpawnerParentInfo : ConditionalTraitInfo
	{
		[ActorReference]
		[FieldLoader.Require]
		[Desc("Spawn these units.")]
		public readonly string[] Actors;

		[Desc("Child actors to contain upon creation. Set to -1 to start with full children.")]
		public readonly int InitialActorCount = -1;

		[Desc("Name of the armaments that grants the LaunchingCondition.",
		"The rate of fire of the dummy weapon determines the launch cycle as each shot.")]
		public readonly HashSet<string> ArmamentNames = ["primary"];

		[Desc("What happens to the children when the parent is killed?")]
		public readonly SpawnerChildDisposal ChildDisposalOnKill = SpawnerChildDisposal.KillChildren;

		[Desc("What happens to the children when the parent is mind controlled?")]
		public readonly SpawnerChildDisposal ChildDisposalOnOwnerChange = SpawnerChildDisposal.GiveChildrenToAttacker;

		[Desc("Only spawn initial load of children?")]
		public readonly bool NoRegeneration = false;

		[Desc("Spawn all children at once when regenerating children, instead of one by one?")]
		public readonly bool SpawnAllAtOnce = false;

		[Desc("Spawn regeneration delay, in ticks")]
		public readonly int RespawnTicks = 150;

		public override void RulesetLoaded(Ruleset rules, ActorInfo ai)
		{
			base.RulesetLoaded(rules, ai);

			if (InitialActorCount > Actors.Length)
				throw new YamlException($"{nameof(InitialActorCount)} can't be larger than the actors defined! (Actor type: {ai.Name})");

			if (InitialActorCount < -1)
				throw new YamlException($"{nameof(InitialActorCount)} must be -1 or non-negative. Actor type: {ai.Name}");
		}

		public override object Create(ActorInitializer init) { return new BaseSpawnerParent(init, this); }
	}

	public class BaseSpawnerParent : ConditionalTrait<BaseSpawnerParentInfo>, INotifyKilled, INotifyOwnerChanged, INotifyActorDisposing
	{
		readonly Actor self;
		protected readonly BaseSpawnerChildEntry[] ChildEntries;

		IFacing facing;

		protected IReloadModifier[] reloadModifiers;

		public BaseSpawnerParent(ActorInitializer init, BaseSpawnerParentInfo info)
			: base(info)
		{
			self = init.Self;

			// Initialize child entries (doesn't instantiate the children yet)
			ChildEntries = CreateChildEntries(info);

			for (var i = 0; i < info.Actors.Length; i++)
			{
				var entry = ChildEntries[i];
				entry.ActorName = info.Actors[i].ToLowerInvariant();
			}
		}

		public virtual BaseSpawnerChildEntry[] CreateChildEntries(BaseSpawnerParentInfo info)
		{
			var childEntries = new BaseSpawnerChildEntry[info.Actors.Length];

			for (var i = 0; i < childEntries.Length; i++)
				childEntries[i] = new BaseSpawnerChildEntry();

			return childEntries;
		}

		protected override void Created(Actor self)
		{
			base.Created(self);

			facing = self.TraitOrDefault<IFacing>();

			reloadModifiers = self.TraitsImplementing<IReloadModifier>().ToArray();
		}

		/// <summary>
		/// Replenish destroyed children or create new ones from nothing.
		/// </summary>
		public void Replenish(Actor self, BaseSpawnerChildEntry[] childEntries)
		{
			if (Info.SpawnAllAtOnce)
			{
				foreach (var childentry in childEntries)
					if (!childentry.IsValid)
						Replenish(self, childentry);
			}
			else
			{
				var entry = SelectEntryToSpawn(childEntries);

				// All are alive and well.
				if (entry == null)
					return;

				Replenish(self, entry);
			}
		}

		/// <summary>
		/// Replenish one child entry.
		/// </summary>
		public virtual void Replenish(Actor self, BaseSpawnerChildEntry entry)
		{
			if (entry.IsValid)
				throw new InvalidOperationException("Replenish must not be run on a valid entry!");

			// Some members are missing. Create a new one.
			var child = self.World.CreateActor(false, entry.ActorName,
				[new OwnerInit(self.Owner)]);

			InitializeChildEntry(child, entry);
			entry.SpawnerChild.LinkParent(entry.Actor, self, this);
		}

		/// <summary>
		/// Child entry initializer function.
		/// Override this function from derived classes to initialize their own specific stuff.
		/// </summary>
		public virtual void InitializeChildEntry(Actor child, BaseSpawnerChildEntry entry)
		{
			entry.Actor = child;
			entry.SpawnerChild = child.Trait<BaseSpawnerChild>();
		}

		protected BaseSpawnerChildEntry SelectEntryToSpawn(BaseSpawnerChildEntry[] childEntries)
		{
			// If any thing is marked dead or null, that's a candidate.
			var candidates = childEntries.Where(m => !m.IsValid).ToList();
			if (candidates.Count == 0)
				return null;

			return candidates.Random(self.World.SharedRandom);
		}

		void INotifyOwnerChanged.OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			OnOwnerChanged(self, oldOwner, newOwner);
		}

		public virtual void OnOwnerChanged(Actor self, Player oldOwner, Player newOwner)
		{
			self.World.AddFrameEndTask(w =>
			{
				foreach (var childEntry in ChildEntries)
					if (childEntry.IsValid)
						childEntry.SpawnerChild.OnParentOwnerChanged(childEntry.Actor, oldOwner, newOwner, Info.ChildDisposalOnOwnerChange);
			});
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			// Just dispose them regardless of child disposal options.
			foreach (var childEntry in ChildEntries)
				if (childEntry.IsValid)
					childEntry.Actor.Dispose();
		}

		public virtual void SpawnIntoWorld(Actor self, Actor child, WPos centerPosition)
		{
			var exit = self.RandomExitOrDefault(self.World, null);
			SetSpawnedFacing(child, exit);

			self.World.AddFrameEndTask(w =>
			{
				if (self.IsDead)
					return;

				foreach (var notify in self.TraitsImplementing<INotifyExitCarrier>())
					notify.Opening(self, child);

				var spawnOffset = WVec.Zero;
				if (exit != null)
					spawnOffset = exit.Info.SpawnOffset.Rotate(new WRot(WAngle.Zero, WAngle.Zero, WAngle.FromFacing(64) - facing.Facing));

				child.Trait<IPositionable>().SetCenterPosition(child, centerPosition + spawnOffset);

				var location = self.World.Map.CellContaining(centerPosition + spawnOffset);

				var move = child.Trait<IMove>();
				child.QueueActivity(move.ReturnToCell(child));

				child.QueueActivity(move.MoveTo(location, 2));

				w.Add(child);
			});
		}

		void SetSpawnedFacing(Actor spawned, Exit exit)
		{
			var facingOffset = facing?.Facing ?? WAngle.Zero;
			var exitFacing = exit?.Info.Facing ?? WAngle.Zero;

			var spawnFacing = spawned.TraitOrDefault<IFacing>();
			if (spawnFacing != null)
				spawnFacing.Facing = facingOffset + exitFacing;
		}

		public virtual void OnChildKilled(Actor self, Actor child) { }

		void INotifyKilled.Killed(Actor self, AttackInfo e)
		{
			foreach (var childEntry in ChildEntries)
				if (childEntry.IsValid)
					childEntry.SpawnerChild.OnParentKilled(childEntry.Actor, e.Attacker, Info.ChildDisposalOnKill);
		}
	}
}
