using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Physics
{
    public static class IHavokContactsJobExtensions
    {
        // IContactsJob.Schedule() implementation for when Havok Physics is available
        public static unsafe JobHandle Schedule<T>(this T jobData, ISimulation simulation, ref PhysicsWorld world, JobHandle inputDeps)
            where T : struct, IContactsJob
        {
            if (simulation.Type == SimulationType.UnityPhysics)
            {
                return IContactsJobExtensions.ScheduleImpl(jobData, simulation, ref world, inputDeps);
            }
            else if (simulation.Type == SimulationType.HavokPhysics)
            {
                var data = new ContactsJobData<T>
                {
                    UserJobData = jobData,
                    ManifoldStream = ((Havok.Physics.HavokSimulation)simulation).ManifoldStream,
                    PluginIndexToLocal = ((Havok.Physics.HavokSimulation)simulation).PluginIndexToLocal,
                    Bodies = world.Bodies
                };
                var parameters = new JobsUtility.JobScheduleParameters(
                    UnsafeUtility.AddressOf(ref data),
                    ContactsJobProcess<T>.Initialize(), inputDeps, ScheduleMode.Batched);
                return JobsUtility.Schedule(ref parameters);
            }
            return inputDeps;
        }

        private unsafe struct ContactsJobData<T> where T : struct
        {
            public T UserJobData;
            [NativeDisableUnsafePtrRestriction] public Havok.Physics.HpBlockStream* ManifoldStream;
            [NativeDisableUnsafePtrRestriction] public Havok.Physics.HpIntArray* PluginIndexToLocal;
            // Disable aliasing restriction in case T has a NativeSlice of PhysicsWorld.Bodies
            [ReadOnly, NativeDisableContainerSafetyRestriction] public NativeSlice<RigidBody> Bodies;
        }

        private struct ContactsJobProcess<T> where T : struct, IContactsJob
        {
            static IntPtr jobReflectionData;

            public static IntPtr Initialize()
            {
                if (jobReflectionData == IntPtr.Zero)
                {
                    jobReflectionData = JobsUtility.CreateJobReflectionData(typeof(ContactsJobData<T>),
                        typeof(T), JobType.Single, (ExecuteJobFunction)Execute);
                }
                return jobReflectionData;
            }

            public delegate void ExecuteJobFunction(ref ContactsJobData<T> jobData, IntPtr additionalData,
                IntPtr bufferRangePatchData, ref JobRanges jobRanges, int jobIndex);

            public unsafe static void Execute(ref ContactsJobData<T> jobData, IntPtr additionalData,
                IntPtr bufferRangePatchData, ref JobRanges jobRanges, int jobIndex)
            {
                var reader = new Havok.Physics.HpBlockStreamReader(jobData.ManifoldStream);
                int* pluginIndexToLocal = jobData.PluginIndexToLocal->Data;
                while (reader.HasItems)
                {
                    var header = (Havok.Physics.HpManifoldStreamHeader*)reader.ReadPtr<Havok.Physics.HpManifoldStreamHeader>();
                    int numManifolds = header->NumManifolds;

                    int bodyAIndex = pluginIndexToLocal[header->BodyIds.BodyAIndex & 0x00ffffff];
                    int bodyBIndex = pluginIndexToLocal[header->BodyIds.BodyBIndex & 0x00ffffff];

                    var userHeader = new ModifiableContactHeader();
                    userHeader.ContactHeader.BodyPair = new BodyIndexPair
                    {
                        BodyAIndex = bodyAIndex,
                        BodyBIndex = bodyBIndex
                    };
                    userHeader.Entities = new EntityPair
                    {
                        EntityA = jobData.Bodies[bodyAIndex].Entity,
                        EntityB = jobData.Bodies[bodyBIndex].Entity
                    };

                    while (numManifolds-- > 0)
                    {
                        var manifold = (Havok.Physics.HpManifold*)reader.ReadPtr<Havok.Physics.HpManifold>();

                        userHeader.ContactHeader.NumContacts = manifold->NumPoints;
                        userHeader.ContactHeader.Normal = manifold->Normal.xyz;
                        var manifoldCache = manifold->m_CollisionCache;
                        userHeader.ContactHeader.CoefficientOfFriction = manifoldCache->m_friction.Value;
                        userHeader.ContactHeader.CoefficientOfRestitution = manifoldCache->m_restitution.Value;
                        userHeader.ContactHeader.ColliderKeys.ColliderKeyA.Value = manifold->m_ShapeKeyA;
                        userHeader.ContactHeader.ColliderKeys.ColliderKeyB.Value = manifold->m_ShapeKeyB;

                        Havok.Physics.HpPerManifoldProperty* cdp = manifoldCache->GetCustomPropertyStorage();
                        userHeader.ContactHeader.BodyCustomTags = cdp->m_bodyTagsPair;
                        userHeader.ContactHeader.JacobianFlags = (JacobianFlags)cdp->m_jacobianFlags;

                        for (int p = 0; p < manifold->NumPoints; p++)
                        {
                            var userContact = new ModifiableContactPoint
                            {
                                Index = p,
                                ContactPoint = new ContactPoint
                                {
                                    Position = new float3(manifold->Positions[p * 4 + 0], manifold->Positions[p * 4 + 1], manifold->Positions[p * 4 + 2]),
                                    Distance = manifold->Distances[p],
                                }
                            };

                            jobData.UserJobData.Execute(ref userHeader, ref userContact);

                            if (userContact.Modified)
                            {
                                manifold->Positions[p * 4 + 0] = userContact.ContactPoint.Position.x;
                                manifold->Positions[p * 4 + 1] = userContact.ContactPoint.Position.y;
                                manifold->Positions[p * 4 + 2] = userContact.ContactPoint.Position.z;
                                manifold->Distances[p] = userContact.ContactPoint.Distance;
                            }

                            if (userHeader.Modified)
                            {
                                manifold->Normal.xyz = userHeader.ContactHeader.Normal;
                                manifoldCache->m_friction.Value = userHeader.ContactHeader.CoefficientOfFriction;
                                manifoldCache->m_restitution.Value = userHeader.ContactHeader.CoefficientOfRestitution;
                                cdp->m_jacobianFlags = (byte)userHeader.ContactHeader.JacobianFlags;

                                if ((cdp->m_jacobianFlags & (byte)JacobianFlags.EnableMassFactors) != 0)
                                {
                                    manifold->m_DataFields |= 1 << 1; // hknpManifold::INERTIA_MODIFIED
                                    manifold->m_DataFields &= 0xfb; // ~CONTAINS_TRIANGLE

                                    var mp = (MassFactors*)UnsafeUtility.AddressOf(ref manifold->m_Scratch[0]);
                                    mp->InvInertiaAndMassFactorA = new float4(1);
                                    mp->InvInertiaAndMassFactorB = new float4(1);
                                }

                                if ((cdp->m_jacobianFlags & (byte)JacobianFlags.IsTrigger) != 0)
                                {
                                    manifold->m_ManifoldType = 1; // hknpManifoldType::TRIGGER
                                }

                                if ((cdp->m_jacobianFlags & (byte)JacobianFlags.EnableSurfaceVelocity) != 0)
                                {
                                    manifoldCache->m_collisionFlags |= 1 << 25; // hknpCollisionFlags::ENABLE_SURFACE_VELOCITY
                                }

                                if (userHeader.ContactHeader.CoefficientOfRestitution != 0)
                                {
                                    manifoldCache->m_collisionFlags |= 1 << 20; // hknpCollisionFlags::ENABLE_RESTITUTION
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}