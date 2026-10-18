"""Example: a row of bollards as instances (one model in the export), with UVs, checked and exported."""
import polymorph as pm

pm.connect()
b = pm.create("cylinder", name="molo_bollard", Radius=0.12, Height=0.9, Sides=12)
pm.add_modifier(b, "Edit Poly")
pm.edit_poly(b, "inset", amount=0.03)
pm.edit_poly(b, "extrude", amount=0.05)
pm.unwrap(b)
pm.set(b, gta={"Role": "Prop"})
pm.instance(b, count=9, offset=[1.5, 0, 0])
pm.frame()
for issue in pm.check():
    print(issue["level"], issue["message"])
print(len(pm.objects(filter="molo_bollard")), "bollards")
