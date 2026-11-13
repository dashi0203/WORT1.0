# 无缝版烘焙：在 WORKFLOW.md 第 11 节 bake_exact.py 基础上改两处，其余参数完全相同。
#  1) 减面前把四条边各简化成 8 段（左右、上下对边高度相同，因为高度是周期采样），
#     这些边界顶点用顶点组保护，Collapse 只减内部 → 对边几何一致。
#  2) 烘焙源用减面后的网格 3×3 平铺，边缘附近的射线打到邻居副本 → 法线左右、上下连续。
# 用法：blender --factory-startup -b -t 4 --python bake_seamless.py -- /path/to/output
# 输入：prepare_exact.py 生成的 <stem>/height.npy；输出：<stem>/Normal_Seamless.png
import bpy, bmesh, numpy as np, pathlib, sys, json
root = pathlib.Path(sys.argv[sys.argv.index('--')+1])
N, SEG = 128, 8               # 网格 128×128；每条边保留 8 段（每 16 格一个点）
for stem in ['IMG_9485','IMG_9473','IMG_9471']:
    p = root/stem; h = np.load(p/'height.npy')
    bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
    def sample(u,v):
        x=u*512-.5; y=(1-v)*512-.5
        ix=int(np.floor(x)); iy=int(np.floor(y)); fx=x-ix; fy=y-iy
        return (h[iy%512,ix%512]*(1-fx)+h[iy%512,(ix+1)%512]*fx)*(1-fy)+(h[(iy+1)%512,ix%512]*(1-fx)+h[(iy+1)%512,(ix+1)%512]*fx)*fy
    verts=[(i/N-.5,j/N-.5,.5*(sample(i/N,j/N)-.5)) for j in range(N+1) for i in range(N+1)]
    faces=[(j*(N+1)+i,j*(N+1)+i+1,(j+1)*(N+1)+i+1,(j+1)*(N+1)+i) for j in range(N) for i in range(N)]
    m=bpy.data.meshes.new('DisplacedHighMesh'); m.from_pydata(verts,[],faces); m.update()
    # 边界简化：只留每 16 格一个点
    bm=bmesh.new(); bm.from_mesh(m); bm.verts.ensure_lookup_table()
    step=N//SEG; kill=[]
    for v in bm.verts:
        i,j=v.index%(N+1),v.index//(N+1)
        onb=i in(0,N) or j in(0,N)
        if onb and not (i%step==0 and j%step==0): kill.append(v)
    bmesh.ops.dissolve_verts(bm,verts=kill)
    bmesh.ops.triangulate(bm,faces=bm.faces[:])
    bm.to_mesh(m); bm.free(); m.update()
    hi=bpy.data.objects.new('DisplacedHigh_Seamless',m); bpy.context.collection.objects.link(hi)
    vg=hi.vertex_groups.new(name='Interior')
    eps=1e-6
    inner=[v.index for v in m.vertices if abs(abs(v.co.x)-.5)>eps and abs(abs(v.co.y)-.5)>eps]
    vg.add(inner,1.0,'REPLACE')
    for f in m.polygons: f.use_smooth=False
    bpy.context.view_layer.objects.active=hi; hi.select_set(True)
    mod=hi.modifiers.new('ActualCollapseDecimation','DECIMATE')
    mod.decimate_type='COLLAPSE'; mod.ratio=.015625; mod.use_collapse_triangulate=True
    mod.vertex_group='Interior'; mod.vertex_group_factor=1000
    bpy.ops.object.modifier_apply(modifier=mod.name)
    for f in hi.data.polygons: f.use_smooth=False
    hi.data.calc_loop_triangles()
    # 自检：对边边界点数与高度是否一致
    def border(sel,key):
        return sorted((round(key(v.co),5),round(v.co.z,5)) for v in hi.data.vertices if sel(v.co))
    L=border(lambda c:abs(c.x+.5)<eps,lambda c:c.y); R=border(lambda c:abs(c.x-.5)<eps,lambda c:c.y)
    B=border(lambda c:abs(c.y+.5)<eps,lambda c:c.x); T=border(lambda c:abs(c.y-.5)<eps,lambda c:c.x)
    ok=(L==R and B==T)
    # 3×3 平铺作为烘焙源
    tiles=[hi]
    for dx in(-1,0,1):
        for dy in(-1,0,1):
            if dx==0 and dy==0: continue
            c=hi.copy(); c.data=hi.data; c.location=(dx,dy,0); bpy.context.collection.objects.link(c); tiles.append(c)
    bpy.ops.mesh.primitive_plane_add(size=1, location=(0,0,-.5))
    lo=bpy.context.object; lo.name='LowPlane_BakeTarget'
    mat=bpy.data.materials.new('BakeTarget'); mat.use_nodes=True; lo.data.materials.append(mat)
    im=bpy.data.images.new(stem+'_Normal',width=512,height=512,alpha=False,float_buffer=False)
    im.colorspace_settings.name='Non-Color'
    node=mat.node_tree.nodes.new('ShaderNodeTexImage'); node.image=im; mat.node_tree.nodes.active=node
    sc=bpy.context.scene; sc.render.engine='CYCLES'; sc.cycles.device='CPU'; sc.cycles.samples=8
    b=sc.render.bake; b.use_selected_to_active=True; b.use_cage=False
    b.cage_extrusion=1; b.max_ray_distance=2; b.margin=0
    b.normal_space='TANGENT'; b.normal_r='POS_X'; b.normal_g='POS_Y'; b.normal_b='POS_Z'
    b.use_clear=True; b.target='IMAGE_TEXTURES'
    bpy.ops.object.select_all(action='DESELECT')
    for t in tiles: t.select_set(True)
    lo.select_set(True); bpy.context.view_layer.objects.active=lo; bpy.ops.object.bake(type='NORMAL')
    im.filepath_raw=str(p/'Normal_Seamless.png'); im.file_format='PNG'; im.save()
    stats={'vertices':len(hi.data.vertices),'triangles':len(hi.data.loop_triangles),
           'smooth_faces':sum(f.use_smooth for f in hi.data.polygons),'border_match':ok,
           'border_points':[len(L),len(R),len(B),len(T)]}
    (p/'mesh_stats_seamless.json').write_text(json.dumps(stats)); print(stem,stats)
    lo.location.z=0
    bpy.ops.wm.save_as_mainfile(filepath=str(p/'Bake_Seamless.blend'))
