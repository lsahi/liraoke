import importlib.util,unittest
spec=importlib.util.spec_from_file_location('worker',str(__import__('pathlib').Path(__file__).resolve().parents[1]/'src/KaraokeFilmMaker.Infrastructure/Alignment/Runtime/alignment-worker.py'))
w=importlib.util.module_from_spec(spec);spec.loader.exec_module(w)
class Tests(unittest.TestCase):
 def run_case(self,raw,fixed=None):
  units=[{'id':str(i)} for i in range(len(raw))]
  if fixed:
   for i,(a,b) in fixed.items():units[i].update(fixedStart=a,fixedEnd=b)
  return w.repair_units([dict(id=str(i),start=a,end=b) for i,(a,b) in enumerate(raw)],units,0,4)
 def test_gap(self):
  p,review,indices=self.run_case([(1,1.5),(2,3)])
  self.assertEqual(p[0]['end'],2);self.assertIsNone(review);self.assertEqual(indices,[])
 def test_missing_between_anchors(self):
  p,review,indices=self.run_case([(0,1),(None,None),(3,4)])
  self.assertEqual((p[1]['start'],p[1]['end']),(1,3));self.assertEqual(indices,[1])
 def test_overlap_review(self):
  p,review,indices=self.run_case([(0,2),(1,2.5),(3,4)])
  self.assertEqual(review,'AI识别失败，推荐人工审核');self.assertEqual(p[1]['start'],2)
 def test_small_overlap(self):
  p,review,_=self.run_case([(0,1.01),(1,2),(3,4)])
  self.assertIsNone(review)
 def test_manual_anchor(self):
  p,_,_=self.run_case([(0,2),(1,3),(3,4)],{1:(2,3)})
  self.assertEqual((p[1]['start'],p[1]['end']),(2,3))
 def test_no_evidence(self):
  with self.assertRaises(ValueError):self.run_case([(None,None),(0,0)])
unittest.main()
