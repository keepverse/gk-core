// global using is per-compilation, not propagated across project references — FusionRpg.Core's own
// alias (Actions/ActionTargetSpec.cs) does not reach this test project, so it needs its own copy.
// buff-debuff-scope T1: ActionRelation/ActionRelations now live in FusionRpg.Contracts as
// RelationKind/RelationKinds; this keeps every existing test file's source text unchanged.
global using ActionRelation = FusionRpg.Contracts.RelationKind;
global using ActionRelations = FusionRpg.Contracts.RelationKinds;
